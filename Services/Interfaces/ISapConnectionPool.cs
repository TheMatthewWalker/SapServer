using SapServer.Configuration;
using SapServer.Models;

namespace SapServer.Services.Interfaces;

public interface ISapConnectionPool
{
    /// <summary>
    /// Executes an ordinary, non-transactional RFC function against NCo's
    /// own internal connection pool. Thread-safe and cheap to call from
    /// anywhere — no dedicated thread or session is held for this call.
    /// </summary>
    Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns a snapshot of every currently-active pinned/elevated session (transient — the stateless pool has no per-connection state to report).</summary>
    IReadOnlyList<WorkerStatus> GetPoolStatus();

    /// <summary>Sends an RFC_PING keep-alive to the stateless pool's destinations. Called by the session monitor.</summary>
    void PingKeepAlive();

    /// <summary>
    /// Claims one pinned session slot (up to SapNco:MaxConcurrentPinnedSessions)
    /// and connects it with the shared service account — queues and waits (up
    /// to SapNco:PinnedAcquireTimeoutSeconds) if all slots are busy. Use this
    /// for a stateful multi-call sequence (a create-BAPI followed by
    /// BAPI_TRANSACTION_COMMIT/ROLLBACK) that must land on the same SAP
    /// session's LUW. The caller MUST release the returned handle via
    /// <see cref="ReleaseWorkerAsync"/>, normally in a finally block.
    /// </summary>
    Task<SapWorkerHandle> AcquireWorkerAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects a pinned session and returns its slot to the pool.</summary>
    Task ReleaseWorkerAsync(SapWorkerHandle worker);

    Task<RfcResponse> ExecuteOnWorkerAsync(
        SapWorkerHandle worker,
        RfcRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims and connects one elevated pinned session with a specific
    /// user's own SAP credentials — queues and waits (up to
    /// SapNco:ElevatedAcquireTimeoutSeconds) if all elevated slots are
    /// currently busy. The caller MUST release the returned handle via
    /// <see cref="ReleaseElevatedWorkerAsync"/>.
    /// </summary>
    Task<SapWorkerHandle> AcquireElevatedWorkerAsync(
        SapConnectionOptions creds,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects an elevated session and returns its slot to the pool.</summary>
    Task ReleaseElevatedWorkerAsync(SapWorkerHandle handle);
}
