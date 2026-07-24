using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Services.Interfaces;
using System.Linq;

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

    // Per-slot bookkeeping so a slot that stays disconnected across many health-check
    // ticks (reconnection is deferred to the next real request, not forced here — see
    // RunHealthCheck below) doesn't re-log the same WARN every single tick and bury
    // real errors in the log. Keyed by SlotId; only ever touched from RunHealthCheck,
    // which BackgroundService guarantees runs on one thread at a time.
    private readonly Dictionary<int, DateTime> _lastDisconnectedWarningAt = new();

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
            else if (s.IsElevated)
            {
                // Expected steady state — elevated slots sit logged out between
                // elevated requests by design, not because of a fault.
            }
            else
            {
                disconnected++;

                var now = DateTime.UtcNow;
                var repeatEvery = TimeSpan.FromSeconds(_options.DisconnectedWarningRepeatSeconds);
                bool alreadyWarned = _lastDisconnectedWarningAt.TryGetValue(s.SlotId, out var lastWarnedAt);

                if (!alreadyWarned || now - lastWarnedAt >= repeatEvery)
                {
                    _logger.LogWarning(
                        "SAP slot {SlotId} is DISCONNECTED (last seen {LastActivity:u}). " +
                        "It will reconnect automatically on the next incoming request.",
                        s.SlotId, s.LastActivity);
                    _lastDisconnectedWarningAt[s.SlotId] = now;
                }
            }
        }

        // Slot reconnected since the last tick — drop its throttle state so the next
        // disconnect (whenever it happens) is logged immediately again, not suppressed
        // by a stale timestamp from this earlier outage.
        foreach (var slotId in _lastDisconnectedWarningAt.Keys.ToList())
        {
            if (statuses.Any(s => s.SlotId == slotId && s.IsConnected))
                _lastDisconnectedWarningAt.Remove(slotId);
        }

        _logger.LogDebug(
            "SAP pool health: {Connected}/{Total} workers connected.",
            connectedCount, statuses.Count);

        // Send keep-alive pings to connected workers that are approaching the idle timeout
        var pingThreshold = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
        _pool.PingIdleWorkers(pingThreshold);
    }
}
