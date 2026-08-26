using System.Collections.Concurrent;
using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;

namespace SapServer.Services.Nco;

/// <summary>
/// One PINNED SAP session: a dedicated managed thread + one physical NCo
/// connection, alive only for the duration of a single caller's
/// transactional sequence — from NcoConnectionPool.AcquireWorkerAsync/
/// AcquireElevatedWorkerAsync through ReleaseWorkerAsync/
/// ReleaseElevatedWorkerAsync — not a long-lived pool slot.
///
/// Why still a dedicated thread at all, given NCo isn't COM and doesn't need
/// an STA apartment? RfcSessionManager.BeginContext pins the CALLING THREAD
/// to one physical pooled connection — required so a stateful multi-call
/// sequence (a create-BAPI followed by BAPI_TRANSACTION_COMMIT/ROLLBACK)
/// lands on the same SAP session's LUW. That's a SAP-level requirement
/// independent of COM vs NCo. But the pin only needs to live as long as ONE
/// such sequence, not for the app's whole lifetime — this rebuild's first
/// pass held a fixed set of these threads (and their BeginContext pins)
/// open forever, carrying over the old COM-era STA-thread-pool shape
/// unnecessarily. Ordinary, non-transactional RFC calls (the majority of
/// traffic) never touch this class at all — see NcoStatelessPool, which
/// calls straight into NCo's own internal, thread-safe connection pool
/// (RfcConfigParameters.PoolSize/MaxPoolSize) from whatever thread happens
/// to be running, with no pinning and no dedicated thread.
///
/// No auto-reconnect: unlike a stateless call, a pinned sequence that loses
/// its connection mid-way can't safely resume a pending, uncommitted LUW —
/// so the first failure just kills the session (IsConnected flips false and
/// every subsequent queued item on it fails fast). The caller's own
/// commit/rollback logic (every controller using AcquireWorkerAsync already
/// wraps this in try/finally) is what reports that back to the user.
///
/// UNVERIFIED: BeginContext/EndContext's exact pinning behavior is documented
/// SAP NCo API, not something this sandbox (no live SAP, no real NCo DLLs)
/// can exercise. Validate a real create+commit sequence against a live
/// system before trusting any transactional/elevated endpoint in production.
/// </summary>
internal sealed class NcoWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<NcoWorkItem> _queue;
    private readonly NcoDestinationRegistry _registry;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SapConnectionOptions _creds;
    private readonly string _destinationName;
    private readonly TaskCompletionSource<bool> _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RfcDestination? _destination;
    private volatile bool _isConnected;

    public int      SlotId       { get; }
    public bool     IsElevated   { get; }
    public bool     IsConnected  => _isConnected;
    public int      QueueDepth   => _queue.Count;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    private NcoWorker(
        int slotId, int maxQueueDepth, NcoDestinationRegistry registry,
        SapConnectionOptions creds, bool isElevated, ILogger logger)
    {
        SlotId           = slotId;
        IsElevated       = isElevated;
        _registry        = registry;
        _creds           = creds;
        _logger          = logger;
        // Always a fresh, uniquely-named destination — never reused across
        // sessions, elevated or not — so a later session can never resolve
        // against another session's still-cached destination/credentials.
        _destinationName = $"PINNED_{slotId}_{Guid.NewGuid():N}";
        _queue           = new BlockingCollection<NcoWorkItem>(maxQueueDepth);

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name         = $"SAP-NCO-PINNED-{slotId}{(isElevated ? "-ELEVATED" : "")}"
        };
        _thread.Start();
    }

    /// <summary>
    /// Constructs and connects a new pinned session, awaiting the connect
    /// (which must run on the session's own dedicated thread — see
    /// WorkerLoop) before returning. Throws SapConnectionException if the
    /// connect fails; the caller does not need to Dispose a worker that
    /// never successfully connected.
    /// </summary>
    public static async Task<NcoWorker> ConnectAsync(
        int slotId, int maxQueueDepth, NcoDestinationRegistry registry,
        SapConnectionOptions creds, bool isElevated, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var worker = new NcoWorker(slotId, maxQueueDepth, registry, creds, isElevated, logger);

        using var reg = cancellationToken.Register(
            () => worker._readyTcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false);

        try
        {
            await worker._readyTcs.Task.ConfigureAwait(false);
        }
        catch
        {
            worker.Dispose();
            throw;
        }

        return worker;
    }

    public void Enqueue(NcoWorkItem item)
    {
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException($"Pinned SAP NCo session {SlotId}'s queue is full.");
    }

    // -------------------------------------------------------------------------
    // Worker thread loop
    // -------------------------------------------------------------------------

    private void WorkerLoop()
    {
        try
        {
            Connect();
            _isConnected = true;
            _readyTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pinned SAP NCo session {SlotId} failed to connect.", SlotId);
            _readyTcs.TrySetException(ex);
            return; // never connected — nothing queued, nothing to disconnect
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                ProcessItem(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path
        }

        Disconnect();
    }

    private void ProcessItem(NcoWorkItem item)
    {
        int threadId = Environment.CurrentManagedThreadId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("RFC '{Function}' starting on pinned session {SlotId}, thread {ThreadId}.",
            item.Request.FunctionName, SlotId, threadId);

        try
        {
            if (!_isConnected)
                throw new SapConnectionException(SlotId,
                    "Pinned SAP NCo session already lost its connection earlier in this sequence — not reconnecting mid-transaction.");

            var response  = NcoRfcExecutor.Execute(_destination!, item.Request, SlotId);
            LastActivity  = DateTime.UtcNow;
            item.Tcs.TrySetResult(response);
        }
        catch (SapConnectionException ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "RFC '{Function}' failed on pinned session {SlotId} — session is now dead.",
                item.Request.FunctionName, SlotId);
            item.Tcs.TrySetException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RFC '{Function}' failed on pinned session {SlotId}.",
                item.Request.FunctionName, SlotId);
            item.Tcs.TrySetException(ex);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation("RFC '{Function}' finished on pinned session {SlotId}, thread {ThreadId} in {ElapsedMs}ms — {Outcome}.",
                item.Request.FunctionName, SlotId, threadId, stopwatch.ElapsedMilliseconds,
                item.Tcs.Task.Status == TaskStatus.RanToCompletion ? "OK" : "FAILED"); // net48 lacks Task.IsCompletedSuccessfully
        }
    }

    // -------------------------------------------------------------------------
    // NCo connection management (must run on _thread)
    // -------------------------------------------------------------------------

    private void Connect()
    {
        try
        {
            _registry.Register(_destinationName, _creds);
            _destination = RfcDestinationManager.GetDestination(_destinationName);
            RfcSessionManager.BeginContext(_destination);
            _destination.Ping();

            _logger.LogInformation("Pinned SAP NCo session {SlotId} connected as '{User}'.", SlotId, _creds.User);
        }
        catch (Exception ex) when (ex is not SapConnectionException)
        {
            throw new SapConnectionException(SlotId, "Failed to establish pinned SAP NCo session.", ex);
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_destination is not null)
                RfcSessionManager.EndContext(_destination);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ending SAP NCo session context on pinned session {SlotId}.", SlotId);
        }
        finally
        {
            _registry.Unregister(_destinationName);
            _destination  = null;
            _isConnected  = false;
            _logger.LogInformation("Pinned SAP NCo session {SlotId} closed.", SlotId);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _queue.Dispose();
    }
}
