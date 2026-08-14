namespace SapServer.Configuration;

public sealed class SapPoolOptions
{
    public const string SectionName = "SapPool";

    /// <summary>
    /// Number of "service" STA worker threads — always logged in (as either
    /// <see cref="ServiceAccount"/> or one entry of <see cref="ServiceAccounts"/>)
    /// for the lifetime of the process. These handle every ordinary RFC call
    /// (ExecuteAsync/AcquireWorker), exactly as the old single pool did.
    /// </summary>
    public int ServiceWorkerCount { get; init; } = 4;

    /// <summary>
    /// Number of "elevated" STA worker threads — created logged OUT, and
    /// only logged in on demand with a specific user's own SAP credentials
    /// for the duration of one elevated request (e.g. PO creation), then
    /// logged back out. This is what lets a route run under the calling
    /// user's real SAP authorization instead of the shared service account.
    /// See SapStaWorker's elevated-mode constructor and
    /// SapConnectionPool.AcquireElevatedWorkerAsync/ReleaseElevatedWorkerAsync.
    /// </summary>
    public int ElevatedWorkerCount { get; init; } = 2;

    /// <summary>
    /// How long a caller will queue and wait for a free elevated worker
    /// before giving up, if all <see cref="ElevatedWorkerCount"/> slots are
    /// already busy with another user's elevated request.
    /// </summary>
    public int ElevatedAcquireTimeoutSeconds { get; init; } = 30;

    /// <summary>Total STA threads started at startup (service + elevated) — the server should be run with at least this many available threads.</summary>
    public int TotalWorkerCount => ServiceWorkerCount + ElevatedWorkerCount;

    /// <summary>Maximum number of queued work items per worker before rejecting new requests.</summary>
    public int MaxQueueDepth { get; init; } = 50;

    /// <summary>
    /// Seconds of inactivity after which the session monitor sends an RFC_PING keep-alive.
    /// SAP GUI typically disconnects idle sessions after ~10 minutes.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 300;

    /// <summary>How often (seconds) the background session monitor runs its health check.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Minimum seconds between repeated "slot N is DISCONNECTED" warnings for
    /// the same still-disconnected slot. The health check runs every
    /// <see cref="HealthCheckIntervalSeconds"/>, so without this a slot that
    /// simply isn't getting traffic (reconnection is deferred to the next real
    /// request — see SapSessionMonitor) logs the same warning every tick,
    /// burying real errors in the log. The first disconnect is always logged
    /// immediately regardless of this value.
    /// </summary>
    public int DisconnectedWarningRepeatSeconds { get; init; } = 600;

    /// <summary>Milliseconds to wait before retrying after a failed reconnection attempt.</summary>
    public int ReconnectDelayMs { get; init; } = 5000;

    /// <summary>
    /// SAP service-account credentials used by every service (non-elevated) pool
    /// worker when <see cref="ServiceAccounts"/> is empty/unset. Also the source
    /// of System/Client/SystemId/Language for elevated-credential building at
    /// PurchasingController/PackagingController's "-elevated" endpoints — those
    /// only need the shared system connection profile, not a specific login, so
    /// they read this field regardless of whether ServiceAccounts is in use.
    /// </summary>
    public SapConnectionOptions ServiceAccount { get; init; } = new();

    /// <summary>
    /// Optional per-worker service accounts. When non-empty, service worker i
    /// logs in as <c>ServiceAccounts[i % ServiceAccounts.Count]</c> instead of
    /// the single shared <see cref="ServiceAccount"/> — lets each service STA
    /// thread hold its own SAP GUI session under a distinct SAP user (e.g. to
    /// avoid whatever behavior your SAP system has for the same account logging
    /// in multiple times concurrently, or to keep each thread's postings
    /// separately attributable in SAP's own change logs). Falls back to
    /// <see cref="ServiceAccount"/> for every worker when empty (the original,
    /// single-shared-account behavior) — existing config with no
    /// <c>ServiceAccounts</c> section needs no changes.
    /// </summary>
    public IReadOnlyList<SapConnectionOptions> ServiceAccounts { get; init; } = Array.Empty<SapConnectionOptions>();
}

public sealed class SapConnectionOptions
{
    public string System   { get; init; } = string.Empty;
    public string Client   { get; init; } = string.Empty;
    public string SystemId { get; init; } = string.Empty;
    public string User     { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Language { get; init; } = "EN";
    public string ApplicationServer { get; init; } = string.Empty;
}
