using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Services.Nco;

/// <summary>
/// SAP NCo-backed implementation of ISapConnectionPool. Two independent
/// execution paths (see SapNcoOptions' class doc for the full rationale):
///
///   - ExecuteAsync (ordinary, non-transactional calls — the bulk of
///     traffic) runs straight against NcoStatelessPool, which calls into
///     NCo's own thread-safe internal connection pool. No dedicated thread,
///     no session pinning, no per-request acquire/release.
///
///   - AcquireWorkerAsync/AcquireElevatedWorkerAsync + ExecuteOnWorkerAsync
///     (transactional sequences needing RfcSessionManager.BeginContext to
///     pin one thread to one connection for a create-BAPI +
///     COMMIT/ROLLBACK) construct a fresh, single-use NcoWorker on acquire
///     and tear it down on release — bounded by a semaphore, not by a fixed
///     set of always-alive threads.
///
/// The ISapConnectionPool contract (and every domain controller written
/// against it) doesn't distinguish "COM" from "NCo" — only this rebuild's
/// original first pass over-applied the old COM-era always-on-thread-pool
/// shape; this version corrects that while keeping the same public surface
/// controllers already use (AcquireWorkerAsync/AcquireElevatedWorkerAsync
/// are now async and require an explicit release, matching the elevated
/// pattern every caller already followed).
///
/// Registered as a singleton so the pool (and the stateless destinations'
/// NCo-managed connections) lives for the application lifetime; pinned/
/// elevated sessions are created and destroyed per request underneath it.
/// </summary>
public sealed class NcoConnectionPool : ISapConnectionPool, IDisposable
{
    private readonly SapNcoOptions _options;
    private readonly NcoDestinationRegistry _registry;
    private readonly NcoStatelessPool _statelessPool;
    private readonly ILogger<NcoConnectionPool> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly SemaphoreSlim _pinnedSemaphore;
    private readonly int _pinnedAcquireTimeoutMs;

    private readonly SemaphoreSlim _elevatedSemaphore;
    private readonly int _elevatedAcquireTimeoutMs;

    // Currently-active pinned + elevated sessions, keyed by slot id — purely
    // for GetPoolStatus() diagnostics; the sessions themselves are otherwise
    // only ever touched via the SapWorkerHandle the caller that acquired
    // them holds.
    private readonly ConcurrentDictionary<int, NcoWorker> _activeSessions = new();
    private int _nextSlotId = -1;

    // RegisterDestinationConfiguration may only be called once per process.
    private static int _configurationRegistered;

    public NcoConnectionPool(
        IOptions<SapNcoOptions> options,
        ILogger<NcoConnectionPool> logger,
        ILoggerFactory loggerFactory)
    {
        _logger        = logger;
        _loggerFactory = loggerFactory;
        _options       = options.Value;

        _registry = new NcoDestinationRegistry(_options);
        if (Interlocked.Exchange(ref _configurationRegistered, 1) == 0)
        {
            RfcDestinationManager.RegisterDestinationConfiguration(_registry);
            logger.LogInformation("SAP NCo destination configuration registered.");
        }

        _statelessPool = new NcoStatelessPool(_options, _registry, loggerFactory.CreateLogger<NcoStatelessPool>());

        _pinnedSemaphore        = new SemaphoreSlim(_options.MaxConcurrentPinnedSessions, _options.MaxConcurrentPinnedSessions);
        _pinnedAcquireTimeoutMs = _options.PinnedAcquireTimeoutSeconds * 1000;

        _elevatedSemaphore        = new SemaphoreSlim(_options.ElevatedWorkerCount, _options.ElevatedWorkerCount);
        _elevatedAcquireTimeoutMs = _options.ElevatedAcquireTimeoutSeconds * 1000;

        logger.LogInformation(
            "SAP NCo pool ready — stateless pool across {Destinations} destination(s) (PoolSize={PoolSize}, MaxPoolSize={MaxPoolSize} each), " +
            "up to {Pinned} concurrent pinned session(s) and {Elevated} concurrent elevated session(s), both connecting on demand per request.",
            _statelessPool.DestinationNames.Count, _options.PoolSize, _options.MaxPoolSize,
            _options.MaxConcurrentPinnedSessions, _options.ElevatedWorkerCount);

        // Logged separately from the line above (and deliberately omits
        // User/Password) specifically so it's easy to visually confirm from
        // the log alone which real SAP system a given running instance is
        // pointed at — e.g. distinguishing a local test SapServer from
        // production when both exist side by side on the same machine.
        var accounts = _options.ServiceAccounts.Count > 0 ? _options.ServiceAccounts : new[] { _options.ServiceAccount };
        for (int i = 0; i < accounts.Count; i++)
        {
            logger.LogInformation(
                "SAP destination POOL_{Index}: AppServerHost={AppServerHost}, SystemNumber={SystemNumber}, Client={Client}",
                i, accounts[i].AppServerHost, accounts[i].SystemNumber, accounts[i].Client);
        }
    }

    /// <inheritdoc/>
    public Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken cancellationToken = default) =>
        _statelessPool.ExecuteAsync(request, cancellationToken);

    public async Task<SapWorkerHandle> AcquireWorkerAsync(CancellationToken cancellationToken = default)
    {
        bool acquired = await _pinnedSemaphore.WaitAsync(_pinnedAcquireTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!acquired)
            throw new PoolExhaustedException(
                $"All {_options.MaxConcurrentPinnedSessions} pinned SAP session slots are busy. " +
                $"Timed out after {_pinnedAcquireTimeoutMs / 1000}s waiting for one to free up — please retry shortly.");

        int slotId = Interlocked.Increment(ref _nextSlotId);
        try
        {
            var worker = await NcoWorker.ConnectAsync(
                slotId, _options.MaxQueueDepth, _registry, _options.ServiceAccount, isElevated: false,
                _loggerFactory.CreateLogger<NcoWorker>(), cancellationToken).ConfigureAwait(false);
            _activeSessions[slotId] = worker;
            return new SapWorkerHandle(worker);
        }
        catch
        {
            _pinnedSemaphore.Release();
            throw;
        }
    }

    public async Task ReleaseWorkerAsync(SapWorkerHandle worker)
    {
        _activeSessions.TryRemove(worker.Worker.SlotId, out _);
        try
        {
            await Task.Run(() => worker.Worker.Dispose()).ConfigureAwait(false);
        }
        finally
        {
            _pinnedSemaphore.Release();
        }
    }

    public async Task<SapWorkerHandle> AcquireElevatedWorkerAsync(
        SapConnectionOptions creds,
        CancellationToken cancellationToken = default)
    {
        bool acquired = await _elevatedSemaphore.WaitAsync(_elevatedAcquireTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!acquired)
            throw new PoolExhaustedException(
                $"All {_options.ElevatedWorkerCount} elevated SAP session slots are busy with other users' requests. " +
                $"Timed out after {_elevatedAcquireTimeoutMs / 1000}s waiting for one to free up — please retry shortly.");

        int slotId = Interlocked.Increment(ref _nextSlotId);
        try
        {
            var worker = await NcoWorker.ConnectAsync(
                slotId, _options.MaxQueueDepth, _registry, creds, isElevated: true,
                _loggerFactory.CreateLogger<NcoWorker>(), cancellationToken).ConfigureAwait(false);
            _activeSessions[slotId] = worker;
            return new SapWorkerHandle(worker);
        }
        catch
        {
            _elevatedSemaphore.Release();
            throw;
        }
    }

    public async Task ReleaseElevatedWorkerAsync(SapWorkerHandle handle)
    {
        _activeSessions.TryRemove(handle.Worker.SlotId, out _);
        try
        {
            await Task.Run(() => handle.Worker.Dispose()).ConfigureAwait(false);
        }
        finally
        {
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
    /// <remarks>Reflects only currently-active pinned/elevated sessions — the always-on stateless pool has no per-connection state NCo exposes to report.</remarks>
    public IReadOnlyList<WorkerStatus> GetPoolStatus() =>
        _activeSessions.Values.Select(w => new WorkerStatus
        {
            SlotId       = w.SlotId,
            IsConnected  = w.IsConnected,
            QueueDepth   = w.QueueDepth,
            LastActivity = w.LastActivity,
            IsElevated   = w.IsElevated
        }).ToList();

    /// <inheritdoc/>
    public void PingKeepAlive() => _statelessPool.PingAll();

    public void Dispose()
    {
        foreach (var worker in _activeSessions.Values)
            worker.Dispose();
        _pinnedSemaphore.Dispose();
        _elevatedSemaphore.Dispose();
    }
}
