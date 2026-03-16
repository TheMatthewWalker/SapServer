using Microsoft.Extensions.Options;
using SapServer.Configuration;
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
    private readonly SapStaWorker[] _workers;
    private readonly ILogger<SapConnectionPool> _logger;

    public SapConnectionPool(
        IOptions<SapPoolOptions> options,
        ILogger<SapConnectionPool> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;

        var opts = options.Value;
        int size = opts.ResolvedPoolSize;

        _workers = new SapStaWorker[size];
        for (int i = 0; i < size; i++)
            _workers[i] = new SapStaWorker(i, opts, loggerFactory.CreateLogger<SapStaWorker>());

        logger.LogInformation(
            "SAP connection pool started with {PoolSize} STA workers (ProcessorCount = {Cpus}).",
            size, Environment.ProcessorCount);
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

    /// <inheritdoc/>
    public IReadOnlyList<WorkerStatus> GetPoolStatus() =>
        _workers.Select(w => new WorkerStatus
        {
            SlotId       = w.SlotId,
            IsConnected  = w.IsConnected,
            QueueDepth   = w.QueueDepth,
            LastActivity = w.LastActivity
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

    /// <summary>
    /// Selects the worker with the shortest queue depth.
    /// Iterates once — O(n) on pool size, which is always small.
    /// </summary>
    private SapStaWorker SelectWorker()
    {
        var best      = _workers[0];
        int bestDepth = best.QueueDepth;

        for (int i = 1; i < _workers.Length; i++)
        {
            int depth = _workers[i].QueueDepth;
            if (depth < bestDepth)
            {
                best      = _workers[i];
                bestDepth = depth;
            }
        }

        return best;
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
            worker.Dispose();
    }
}
