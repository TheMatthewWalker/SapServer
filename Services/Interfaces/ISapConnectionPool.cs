using SapServer.Models;

namespace SapServer.Services.Interfaces;

public interface ISapConnectionPool
{
    /// <summary>
    /// Executes an RFC function on the least-loaded available STA worker.
    /// Awaitable from any thread — the actual SAP call runs on the worker's STA thread.
    /// </summary>
    Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns a snapshot of each pool worker's current health and load.</summary>
    IReadOnlyList<WorkerStatus> GetPoolStatus();

    /// <summary>
    /// Sends an RFC_PING keep-alive to every worker whose last activity exceeds
    /// <paramref name="idleThreshold"/>. Called by the session monitor.
    /// </summary>
    void PingIdleWorkers(TimeSpan idleThreshold);
}
