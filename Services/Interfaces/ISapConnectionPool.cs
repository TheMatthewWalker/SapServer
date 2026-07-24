using SapServer.Configuration;
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


    SapWorkerHandle AcquireWorker();

    Task<RfcResponse> ExecuteOnWorkerAsync(
        SapWorkerHandle worker,
        RfcRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims and logs in one elevated worker slot with a specific user's own
    /// SAP credentials — queues and waits (up to
    /// <c>SapPool:ElevatedAcquireTimeoutSeconds</c>) if all elevated slots are
    /// currently busy. See SapConnectionPool for the full contract; the caller
    /// MUST release the returned handle via <see cref="ReleaseElevatedWorkerAsync"/>.
    /// </summary>
    Task<SapWorkerHandle> AcquireElevatedWorkerAsync(
        SapConnectionOptions creds,
        CancellationToken cancellationToken = default);

    /// <summary>Logs an elevated worker back out and returns its slot to the pool.</summary>
    Task ReleaseElevatedWorkerAsync(SapWorkerHandle handle);
}
