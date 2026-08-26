using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Services.Nco;

/// <summary>
/// SAP NCo-backed implementation of ISapConnectionPool — replaces the old
/// COM/STA-thread SapConnectionPool entirely (see CLAUDE.md). Routing
/// strategy, elevated acquire/release semantics, and the ISapConnectionPool
/// contract itself are unchanged from the COM version; every domain
/// controller was written against that interface, not against COM directly,
/// so this swap needed no controller-level redesign beyond the mechanical
/// ASP.NET-Core-to-WebApi2 port.
///
/// Registered as a singleton so the pool (and its worker threads + NCo
/// connections) lives for the application lifetime.
/// </summary>
public sealed class NcoConnectionPool : ISapConnectionPool, IDisposable
{
    private readonly NcoWorker[] _workers;
    private readonly NcoWorker[] _elevatedWorkers;

    private readonly SemaphoreSlim _elevatedSemaphore;
    private readonly ConcurrentQueue<NcoWorker> _elevatedFreeList;
    private readonly int _elevatedAcquireTimeoutMs;

    private readonly NcoDestinationRegistry _registry;
    private readonly ILogger<NcoConnectionPool> _logger;

    // RegisterDestinationConfiguration may only be called once per process.
    private static int _configurationRegistered;

    public NcoConnectionPool(
        IOptions<SapNcoOptions> options,
        ILogger<NcoConnectionPool> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        var opts = options.Value;

        _registry = new NcoDestinationRegistry(opts);
        if (Interlocked.Exchange(ref _configurationRegistered, 1) == 0)
        {
            RfcDestinationManager.RegisterDestinationConfiguration(_registry);
            logger.LogInformation("SAP NCo destination configuration registered.");
        }

        _workers = new NcoWorker[opts.ServiceWorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            var account = opts.ServiceAccounts.Count > 0
                ? opts.ServiceAccounts[i % opts.ServiceAccounts.Count]
                : null; // NcoWorker falls back to opts.ServiceAccount itself when null
            _workers[i] = new NcoWorker(i, opts, _registry, loggerFactory.CreateLogger<NcoWorker>(), serviceAccount: account);
        }

        _elevatedWorkers = new NcoWorker[opts.ElevatedWorkerCount];
        for (int i = 0; i < _elevatedWorkers.Length; i++)
            _elevatedWorkers[i] = new NcoWorker(
                opts.ServiceWorkerCount + i, opts, _registry, loggerFactory.CreateLogger<NcoWorker>(), isElevated: true);

        _elevatedSemaphore        = new SemaphoreSlim(_elevatedWorkers.Length, _elevatedWorkers.Length);
        _elevatedFreeList         = new ConcurrentQueue<NcoWorker>(_elevatedWorkers);
        _elevatedAcquireTimeoutMs = opts.ElevatedAcquireTimeoutSeconds * 1000;

        logger.LogInformation(
            "SAP NCo connection pool started with {ServiceCount} service workers (always connected) " +
            "and {ElevatedCount} elevated workers (unconnected, per-user on demand) — {Total} threads total.",
            _workers.Length, _elevatedWorkers.Length, opts.TotalWorkerCount);
    }

    /// <inheritdoc/>
    public async Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken cancellationToken = default)
    {
        var worker = SelectWorker();

        _logger.LogInformation(
            "RFC '{Function}' routed to slot {SlotId} (queue depth {QueueDepth} before enqueue).",
            request.FunctionName, worker.SlotId, worker.QueueDepth);

        var tcs  = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new NcoWorkItem(request, tcs, cancellationToken);

        using var reg = cancellationToken.Register(
            () => tcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false);

        worker.Enqueue(item);
        return await tcs.Task.ConfigureAwait(false);
    }

    // For advanced scenarios where the caller needs a sequence of calls pinned
    // to the same physical SAP session/connection — e.g. a create-BAPI
    // followed by BAPI_TRANSACTION_COMMIT/ROLLBACK, which must land on the
    // same LUW. See NcoWorker's class doc comment for why this still needs a
    // dedicated worker thread under NCo, not just "any pooled connection".
    public SapWorkerHandle AcquireWorker()
    {
        var worker = SelectWorker();
        return new SapWorkerHandle(worker);
    }

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
            _elevatedSemaphore.Release();
            throw new InvalidOperationException(
                "Elevated SAP NCo worker semaphore and free-list are out of sync — this is a bug.");
        }

        try
        {
            await worker.LogonElevatedAsync(creds).ConfigureAwait(false);
        }
        catch
        {
            _elevatedFreeList.Enqueue(worker);
            _elevatedSemaphore.Release();
            throw;
        }

        return new SapWorkerHandle(worker);
    }

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
                "Error disconnecting elevated SAP NCo slot {SlotId} on release — releasing the slot back to the pool anyway.",
                worker.SlotId);
        }
        finally
        {
            _elevatedFreeList.Enqueue(worker);
            _elevatedSemaphore.Release();
        }
    }

    public async Task<RfcResponse> ExecuteOnWorkerAsync(
        SapWorkerHandle handle,
        RfcRequest request,
        CancellationToken ct = default)
    {
        var worker = handle.Worker;

        var tcs  = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new NcoWorkItem(request, tcs, ct);

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

    private int _nextWorkerIndex = -1;

    /// <summary>
    /// Selects the worker with the shortest queue depth, round-robining among
    /// every worker currently tied at that minimum — same two-pass
    /// least-loaded-with-rotating-tiebreak logic as the old COM pool's
    /// SelectWorker (a plain first-minimum-wins scan previously serialized
    /// bursts of concurrent calls onto slot 0; see that history in the COM
    /// implementation this replaced).
    /// </summary>
    private NcoWorker SelectWorker()
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
