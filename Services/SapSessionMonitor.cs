using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Background service that periodically checks SAP connection health and
/// sends RFC_PING keep-alives to idle workers.
///
/// Two problems it prevents:
///   1. SAP idle-timeout disconnection — SAP GUI sessions disconnect after a
///      configurable period of inactivity (typically 10–15 minutes). Pinging
///      before that threshold resets the idle timer.
///   2. Silent connection loss — network or SAP restarts can silently drop
///      connections. The monitor logs disconnected workers so operators are
///      aware before the next real request hits them.
///
/// Reconnection on a disconnected slot is deferred to the next actual request
/// via <see cref="SapStaWorker.EnsureConnected"/>; the monitor does not force
/// reconnect itself so it cannot accidentally block the HTTP pipeline.
/// </summary>
public sealed class SapSessionMonitor : BackgroundService
{
    private readonly ISapConnectionPool _pool;
    private readonly SapPoolOptions _options;
    private readonly ILogger<SapSessionMonitor> _logger;

    public SapSessionMonitor(
        ISapConnectionPool pool,
        IOptions<SapPoolOptions> options,
        ILogger<SapSessionMonitor> logger)
    {
        _pool    = pool;
        _options = options.Value;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SAP session monitor started. Health-check every {Interval}s, idle ping after {Idle}s.",
            _options.HealthCheckIntervalSeconds, _options.IdleTimeoutSeconds);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RunHealthCheck();
        }
    }

    private void RunHealthCheck()
    {
        var statuses        = _pool.GetPoolStatus();
        int connectedCount  = 0;
        int disconnected    = 0;

        foreach (var s in statuses)
        {
            if (s.IsConnected) connectedCount++;
            else
            {
                disconnected++;
                _logger.LogWarning(
                    "SAP slot {SlotId} is DISCONNECTED (last seen {LastActivity:u}). " +
                    "It will reconnect automatically on the next incoming request.",
                    s.SlotId, s.LastActivity);
            }
        }

        _logger.LogDebug(
            "SAP pool health: {Connected}/{Total} workers connected.",
            connectedCount, statuses.Count);

        // Send keep-alive pings to connected workers that are approaching the idle timeout
        var pingThreshold = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
        _pool.PingIdleWorkers(pingThreshold);
    }
}
