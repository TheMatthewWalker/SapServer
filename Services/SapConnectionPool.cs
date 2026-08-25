using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Manages a fixed pool of <see cref="SapStaWorker"/> instances.
///
/// Routing strategy: least-loaded worker (fewest items in queue), with
/// round-robin tiebreaking so load stays even when all workers are idle.
///
/// Registered as a singleton so the pool (and its STA threads + SAP connections)
/// lives for the application lifetime.
/// </summary>
public sealed class SapConnectionPool : ISapConnectionPool, IDisposable
{
    // "Service" workers — always logged in as the shared service account.
    // ExecuteAsync/AcquireWorker (every ordinary RFC call) only ever route here.
    private readonly SapStaWorker[] _workers;

    // "Elevated" workers — start logged OUT, only logged in on demand with a
    // specific user's own SAP credentials for the duration of one elevated
    // request (see task #413/#414: AcquireElevatedWorkerAsync/ReleaseElevatedWorkerAsync).
    // Kept in a separate array so SelectWorker() (used by every normal call)
    // can never accidentally route ordinary traffic onto one of these slots.
    private readonly SapStaWorker[] _elevatedWorkers;

    // Gates concurrent elevated acquisitions to exactly the number of elevated
    // worker slots — WaitAsync(timeout) is what implements the agreed
    // "queue and wait, with a timeout" behavior for a 4th+ concurrent caller.
    private readonly SemaphoreSlim _elevatedSemaphore;

    // Which elevated workers are currently unclaimed. Kept in lockstep with
    // _elevatedSemaphore's count: a successful WaitAsync is always followed by
    // exactly one TryDequeue, and a Release is always preceded by exactly one Enqueue.
    private readonly ConcurrentQueue<SapStaWorker> _elevatedFreeList;

    private readonly int _elevatedAcquireTimeoutMs;

    private readonly ILogger<SapConnectionPool> _logger;

    public SapConnectionPool(
        IOptions<SapPoolOptions> options,
        ILogger<SapConnectionPool> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;

        var opts = options.Value;

        // When SapPool:ServiceAccounts is populated, each service worker gets its
        // own SAP login (wrapping round-robin if there are fewer accounts than
        // workers) instead of every worker sharing the single ServiceAccount —
        // see SapPoolOptions.ServiceAccounts and SapStaWorker's serviceAccount ctor
        // param. An empty/unset list falls back to the original single-account
        // behavior for every worker.
        _workers = new SapStaWorker[opts.ServiceWorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            var account = opts.ServiceAccounts.Count > 0
                ? opts.ServiceAccounts[i % opts.ServiceAccounts.Count]
                : null; // SapStaWorker falls back to opts.ServiceAccount itself when null
            _workers[i] = new SapStaWorker(i, opts, loggerFactory.CreateLogger<SapStaWorker>(), serviceAccount: account);
        }

        // Elevated slot IDs continue on from the service slot IDs so every
        // worker in the pool has a unique SlotId for logging/health-status purposes.
        _elevatedWorkers = new SapStaWorker[opts.ElevatedWorkerCount];
        for (int i = 0; i < _elevatedWorkers.Length; i++)
            _elevatedWorkers[i] = new SapStaWorker(
                opts.ServiceWorkerCount + i, opts, loggerFactory.CreateLogger<SapStaWorker>(), isElevated: true);

        _elevatedSemaphore        = new SemaphoreSlim(_elevatedWorkers.Length, _elevatedWorkers.Length);
        _elevatedFreeList         = new ConcurrentQueue<SapStaWorker>(_elevatedWorkers);
        _elevatedAcquireTimeoutMs = opts.ElevatedAcquireTimeoutSeconds * 1000;

        logger.LogInformation(
            "SAP connection pool started with {ServiceCount} service STA workers (always logged in) " +
            "and {ElevatedCount} elevated STA workers (logged out, per-user on demand) — {Total} threads total.",
            _workers.Length, _elevatedWorkers.Length, opts.TotalWorkerCount);
    }

    /// <inheritdoc/>
    public async Task<RfcResponse> ExecuteAsync(
        RfcRequest request,
        CancellationToken cancellationToken = default)
    {
        var worker = SelectWorker();

        var tcs    = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item   = new SapWorkItem(request, tcs, cancellationToken);

        // Wire cancellation so the caller can time out without leaking
        using var reg = cancellationToken.Register(
            () => tcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false);

        worker.Enqueue(item);
        return await tcs.Task.ConfigureAwait(false);
    }

    // For advanced scenarios where the caller needs direct access to a worker's STA thread,
    // e.g. to execute multiple calls in sequence without returning to the thread pool.
    public SapWorkerHandle AcquireWorker()
    {
        var worker = SelectWorker();
        return new SapWorkerHandle(worker);
    }


    /// <summary>
    /// Claims one elevated worker slot and logs it in with <paramref name="creds"/>
    /// — one specific user's own SAP username/password, decrypted just-in-time by
    /// the caller (see lib/sapCredentials.js in the sql2005-bridge Node app).
    ///
    /// If all elevated slots are already busy with other users' requests, this
    /// queues and waits up to <c>SapPool:ElevatedAcquireTimeoutSeconds</c> for one
    /// to free up (the agreed behavior for a 4th+ concurrent elevated caller),
    /// then throws <see cref="PoolExhaustedException"/>.
    ///
    /// The caller MUST release the returned handle via
    /// <see cref="ReleaseElevatedWorkerAsync"/> in a finally block — that's what
    /// logs the slot back out so it can never be reused by a different user.
    /// </summary>
    public async Task<SapWorkerHandle> AcquireElevatedWorkerAsync(
        SapConnectionOptions creds,
        CancellationToken ct = default)
    {
        bool acquired = await _elevatedSemaphore.WaitAsync(_elevatedAcquireTimeoutMs, ct).ConfigureAwait(false);
        if (!acquired)
            throw new PoolExhaustedException(
                $"All {_elevatedWorkers.Length} elevated SAP worker slots are busy with other users' requests. " +
                $"Timed out after {_elevatedAcquireTimeoutMs / 1000}s waiting for one to free up — please retry shortly.");

        if (!_elevatedFreeList.TryDequeue(out var worker))
        {
            // Should never happen — the semaphore count and free-list length are
            // kept in lockstep by every acquire/release path. Guard anyway rather
            // than silently leaking a permit.
            _elevatedSemaphore.Release();
            throw new InvalidOperationException(
                "Elevated SAP worker semaphore and free-list are out of sync — this is a bug.");
        }

        try
        {
            await worker.LogonElevatedAsync(creds).ConfigureAwait(false);
        }
        catch
        {
            // Logon failed (bad credentials, SAP unreachable, etc.) — hand the
            // slot straight back so the failure doesn't cost the pool a permanent
            // elevated slot.
            _elevatedFreeList.Enqueue(worker);
            _elevatedSemaphore.Release();
            throw;
        }

        return new SapWorkerHandle(worker);
    }

    /// <summary>
    /// Releases a handle acquired via <see cref="AcquireElevatedWorkerAsync"/> —
    /// logs the worker back out and returns the slot to the free pool. Always
    /// call this in a finally block, on both the success and failure path of
    /// whatever elevated request the handle was used for, so a failed request
    /// never leaves a slot logged in as one user indefinitely.
    /// </summary>
    public async Task ReleaseElevatedWorkerAsync(SapWorkerHandle handle)
    {
        var worker = handle.Worker;
        try
        {
            await worker.LogoffElevatedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error logging off elevated SAP slot {SlotId} on release — releasing the slot back to the pool anyway.",
                worker.SlotId);
        }
        finally
        {
            _elevatedFreeList.Enqueue(worker);
            _elevatedSemaphore.Release();
        }
    }

    // Executes a request directly on the specified worker's STA thread.
    public async Task<RfcResponse> ExecuteOnWorkerAsync(
        SapWorkerHandle handle,
        RfcRequest request,
        CancellationToken ct = default)
    {
        var worker = handle.Worker;

        var tcs = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new SapWorkItem(request, tcs, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        worker.Enqueue(item);
        return await tcs.Task.ConfigureAwait(false);
    }



    /// <inheritdoc/>
    public IReadOnlyList<WorkerStatus> GetPoolStatus() =>
        _workers.Concat(_elevatedWorkers).Select(w => new WorkerStatus
        {
            SlotId       = w.SlotId,
            IsConnected  = w.IsConnected,
            QueueDepth   = w.QueueDepth,
            LastActivity = w.LastActivity,
            IsElevated   = w.IsElevated
        }).ToList();

    /// <inheritdoc/>
    public void PingIdleWorkers(TimeSpan idleThreshold)
    {
        // Elevated workers are deliberately excluded — logged-out/idle is their
        // normal steady state between requests, and pinging one would require
        // logging it in, which only AcquireElevatedWorkerAsync should ever do.
        var cutoff = DateTime.UtcNow - idleThreshold;
        foreach (var worker in _workers)
        {
            if (worker.IsConnected && worker.LastActivity < cutoff)
            {
                _logger.LogDebug(
                    "Slot {SlotId} idle since {LastActivity:u}, sending keep-alive ping.",
                    worker.SlotId, worker.LastActivity);
                worker.Ping();
            }
        }
    }

    // Rotating start point for the tie-break scan below — Interlocked.Increment
    // gives each concurrent SelectWorker() call a distinct starting offset
    // without needing a lock. Wraps naturally (int overflow); only ever used
    // mod _workers.Length, so the sign/magnitude after overflow don't matter.
    private int _nextWorkerIndex = -1;

    /// <summary>
    /// Selects the worker with the shortest queue depth, round-robining among
    /// every worker currently tied at that minimum — this is the
    /// round-robin-on-ties behavior this class's doc comment has always
    /// promised, but a prior version of this method was a plain
    /// first-minimum-wins scan with no tiebreak at all, so a burst of
    /// concurrent calls that all observed every worker idle (queue depth 0 —
    /// the common case) all landed on _workers[0], serializing work that
    /// should have spread across the pool. Two passes: one to find the
    /// minimum depth, one starting from a rotating offset to pick among the
    /// workers actually at it.
    /// </summary>
    private SapStaWorker SelectWorker()
    {
        int n = _workers.Length;
        int minDepth = _workers[0].QueueDepth;
        for (int i = 1; i < n; i++)
        {
            int depth = _workers[i].QueueDepth;
            if (depth < minDepth) minDepth = depth;
        }

        int start = Interlocked.Increment(ref _nextWorkerIndex);
        for (int offset = 0; offset < n; offset++)
        {
            int idx = (int)(((uint)(start + offset)) % n);
            if (_workers[idx].QueueDepth == minDepth)
                return _workers[idx];
        }

        // Unreachable — minDepth was just observed on at least one worker
        // above, so the loop always returns before falling through.
        return _workers[0];
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
            worker.Dispose();
        foreach (var worker in _elevatedWorkers)
            worker.Dispose();
        _elevatedSemaphore.Dispose();
    }
}



public sealed class SapWorkerHandle
{
    internal SapStaWorker Worker { get; }

    internal SapWorkerHandle(SapStaWorker worker)
    {
        Worker = worker;
    }
}
