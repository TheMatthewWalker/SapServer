namespace SapServer.Configuration;

/// <summary>
/// Configuration for the SAP NCo connection pool (Services/Nco/*) — the sole
/// SAP transport after the .NET Framework 4.8 + NCo rebuild (replaces the old
/// SapPoolOptions/SAPFunctions64 COM shape). See CLAUDE.md's architecture
/// section for the full NcoWorker/NcoConnectionPool design.
/// </summary>
public sealed class SapNcoOptions
{
    public const string SectionName = "SapNco";

    /// <summary>
    /// Number of "service" NCo worker threads — always connected (as either
    /// <see cref="ServiceAccount"/> or one entry of <see cref="ServiceAccounts"/>)
    /// for the lifetime of the process. Each owns one dedicated managed thread
    /// pinned (via RfcSessionManager.BeginContext) to one physical pooled
    /// connection — needed so a create-BAPI + COMMIT/ROLLBACK sequence
    /// (AcquireWorker/ExecuteOnWorkerAsync) lands on the same SAP session's
    /// LUW, a SAP-level requirement independent of COM vs NCo.
    /// </summary>
    public int ServiceWorkerCount { get; init; } = 4;

    /// <summary>
    /// Number of "elevated" NCo worker threads — created unconnected, and only
    /// connected on demand with a specific user's own SAP credentials for the
    /// duration of one elevated request (e.g. PO creation), then disconnected.
    /// </summary>
    public int ElevatedWorkerCount { get; init; } = 2;

    /// <summary>
    /// How long a caller will queue and wait for a free elevated worker
    /// before giving up, if all <see cref="ElevatedWorkerCount"/> slots are
    /// already busy with another user's elevated request.
    /// </summary>
    public int ElevatedAcquireTimeoutSeconds { get; init; } = 30;

    /// <summary>Total worker threads started at startup (service + elevated).</summary>
    public int TotalWorkerCount => ServiceWorkerCount + ElevatedWorkerCount;

    /// <summary>Maximum number of queued work items per worker before rejecting new requests.</summary>
    public int MaxQueueDepth { get; init; } = 50;

    /// <summary>
    /// Seconds of inactivity after which the session monitor sends an
    /// RFC_PING keep-alive — SAP application servers can idle-timeout a
    /// pooled RFC connection much like SAP GUI does a COM session.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 300;

    /// <summary>How often (seconds) the background session monitor runs its health check.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 60;

    /// <summary>Minimum seconds between repeated "slot N is DISCONNECTED" warnings for the same slot.</summary>
    public int DisconnectedWarningRepeatSeconds { get; init; } = 600;

    /// <summary>Milliseconds to wait before retrying after a failed reconnection attempt.</summary>
    public int ReconnectDelayMs { get; init; } = 5000;

    /// <summary>
    /// SAP service-account credentials used by every service (non-elevated)
    /// worker when <see cref="ServiceAccounts"/> is empty/unset. Also the
    /// source of AppServerHost/SystemNumber/Client/Language for
    /// elevated-credential building at PurchasingController/
    /// PackagingController's "-elevated" endpoints — those only need the
    /// shared system connection profile, not a specific login.
    /// </summary>
    public SapConnectionOptions ServiceAccount { get; init; } = new();

    /// <summary>
    /// Optional per-worker service accounts. When non-empty, service worker i
    /// connects as <c>ServiceAccounts[i % ServiceAccounts.Count]</c> instead of
    /// the single shared <see cref="ServiceAccount"/>. Falls back to
    /// <see cref="ServiceAccount"/> for every worker when empty.
    /// </summary>
    public IReadOnlyList<SapConnectionOptions> ServiceAccounts { get; init; } = Array.Empty<SapConnectionOptions>();

    /// <summary>NCo's own internal RFC connection pool size per destination — not a thread count.</summary>
    public int PoolSize { get; init; } = 2;

    /// <summary>Ceiling NCo will grow a destination's connection pool to under load.</summary>
    public int MaxPoolSize { get; init; } = 4;
}

/// <summary>
/// One SAP logon profile. Field names match SAP NCo's RfcConfigParameters
/// directly (AppServerHost+SystemNumber, not SAP GUI's System/SystemId
/// logon-pad concepts the old COM-era SapConnectionOptions used — NCo
/// connects straight to an application server or message server, not through
/// a GUI logon pad entry).
/// </summary>
public sealed class SapConnectionOptions
{
    public string AppServerHost { get; init; } = string.Empty;
    public string SystemNumber  { get; init; } = string.Empty;
    public string Client        { get; init; } = string.Empty;
    public string User          { get; init; } = string.Empty;
    public string Password      { get; init; } = string.Empty;
    public string Language      { get; init; } = "EN";
}
