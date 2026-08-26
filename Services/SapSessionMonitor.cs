using SapServer.Configuration;
using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Periodically sends an RFC_PING keep-alive to the stateless pool's
/// destinations, so SAP's own idle-timeout doesn't drop a pooled connection
/// during a quiet period. Ported off ASP.NET Core's BackgroundService/
/// IHostedService (no generic host under OWIN/System.Web) onto a plain
/// System.Threading.Timer — Start()/Stop() are called explicitly from
/// Startup.cs, with Stop() wired to OWIN's "host.OnAppDisposing" token so
/// the timer doesn't outlive the app pool/host shutting the app down.
///
/// Pinned and elevated sessions are deliberately NOT pinged here — they only
/// exist for the duration of one caller's active request (see
/// NcoConnectionPool/NcoWorker), so there is no idle state for them to fall
/// into between health-check ticks; GetPoolStatus() is used purely for
/// point-in-time diagnostics (api/rfc/status), not for anything this monitor
/// needs to act on.
/// </summary>
public sealed class SapSessionMonitor : IDisposable
{
    private readonly ISapConnectionPool _pool;
    private readonly SapNcoOptions _options;
    private readonly ILogger _logger;
    private Timer? _timer;

    public SapSessionMonitor(ISapConnectionPool pool, SapNcoOptions options, ILogger<SapSessionMonitor> logger)
    {
        _pool    = pool;
        _options = options;
        _logger  = logger;
    }

    public void Start()
    {
        _logger.LogInformation(
            "SAP session monitor started. Keep-alive ping every {Interval}s.",
            _options.HealthCheckIntervalSeconds);

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
        var activeSessions = _pool.GetPoolStatus();
        _logger.LogDebug("SAP pool health: {Active} pinned/elevated session(s) currently active.", activeSessions.Count);

        _pool.PingKeepAlive();
    }

    public void Dispose() => Stop();
}
