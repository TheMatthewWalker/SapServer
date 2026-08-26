using SapServer.Configuration;
using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Periodically checks SAP connection health and sends RFC_PING keep-alives
/// to idle workers. Ported off ASP.NET Core's BackgroundService/IHostedService
/// (no generic host under OWIN/System.Web) onto a plain System.Threading.Timer
/// — Start()/Stop() are called explicitly from Startup.cs, with Stop() wired
/// to OWIN's "host.OnAppDisposing" token so the timer doesn't outlive the app
/// pool/host shutting the app down.
///
/// Two problems it prevents:
///   1. SAP idle-timeout disconnection — application servers can drop an idle
///      pooled RFC connection after a period of inactivity, just as SAP GUI
///      did for COM sessions. Pinging before that threshold resets it.
///   2. Silent connection loss — network or SAP restarts can silently drop
///      connections. The monitor logs disconnected workers so operators are
///      aware before the next real request hits them.
///
/// Reconnection on a disconnected slot is deferred to the next actual request
/// via NcoWorker.EnsureConnected; the monitor does not force reconnect itself
/// so it cannot accidentally block the HTTP pipeline.
/// </summary>
public sealed class SapSessionMonitor : IDisposable
{
    private readonly ISapConnectionPool _pool;
    private readonly SapNcoOptions _options;
    private readonly ILogger _logger;
    private Timer? _timer;

    // Per-slot bookkeeping so a slot that stays disconnected across many
    // health-check ticks doesn't re-log the same WARN every tick. Only ever
    // touched from RunHealthCheck, which the Timer guarantees runs one tick
    // at a time (TimerCallback never re-enters while a previous tick is
    // still running — see Start()'s Timer construction).
    private readonly Dictionary<int, DateTime> _lastDisconnectedWarningAt = new();

    public SapSessionMonitor(ISapConnectionPool pool, SapNcoOptions options, ILogger logger)
    {
        _pool    = pool;
        _options = options;
        _logger  = logger;
    }

    public void Start()
    {
        _logger.LogInformation(
            "SAP session monitor started. Health-check every {Interval}s, idle ping after {Idle}s.",
            _options.HealthCheckIntervalSeconds, _options.IdleTimeoutSeconds);

        var interval = TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds);
        // Timer's own re-entrancy guard (period = Timeout.Infinite, rescheduled
        // at the end of each tick) stands in for BackgroundService's implicit
        // "one execution loop iteration at a time" guarantee.
        _timer = new Timer(_ => RunHealthCheckAndReschedule(), null, interval, Timeout.InfiniteTimeSpan);
    }

    public void Stop() => _timer?.Dispose();

    private void RunHealthCheckAndReschedule()
    {
        try
        {
            RunHealthCheck();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAP session monitor health check failed.");
        }
        finally
        {
            _timer?.Change(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void RunHealthCheck()
    {
        var statuses       = _pool.GetPoolStatus();
        int connectedCount = 0;
        int disconnected   = 0;

        foreach (var s in statuses)
        {
            if (s.IsConnected) connectedCount++;
            else if (s.IsElevated)
            {
                // Expected steady state — elevated slots sit unconnected
                // between elevated requests by design, not because of a fault.
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

        foreach (var slotId in _lastDisconnectedWarningAt.Keys.ToList())
        {
            if (statuses.Any(s => s.SlotId == slotId && s.IsConnected))
                _lastDisconnectedWarningAt.Remove(slotId);
        }

        _logger.LogDebug(
            "SAP pool health: {Connected}/{Total} workers connected.",
            connectedCount, statuses.Count);

        var pingThreshold = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
        _pool.PingIdleWorkers(pingThreshold);
    }

    public void Dispose() => Stop();
}
