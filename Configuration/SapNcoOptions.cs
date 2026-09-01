namespace SapServer.Configuration;

/// <summary>
/// Configuration for the SAP NCo connection pool (Services/Nco/*) — the sole
/// SAP transport after the .NET Framework 4.8 + NCo rebuild (replaces the old
/// SapPoolOptions/SAPFunctions64 COM shape). See CLAUDE.md's architecture
/// section for the full NcoConnectionPool design.
///
/// Two distinct groups, sized independently, matching NcoConnectionPool's
/// two execution paths:
///   - Ordinary, non-transactional RFC calls (ExecuteAsync — the bulk of
///     traffic) run straight against NCo's own internal per-destination
///     connection pool (<see cref="PoolSize"/>/<see cref="MaxPoolSize"/>).
///     No .NET thread is dedicated to this at all; NCo's pool is already
///     thread-safe and handles concurrency internally.
///   - Transactional sequences that must land on one SAP session's LUW (a
///     create-BAPI followed by BAPI_TRANSACTION_COMMIT/ROLLBACK, via
///     AcquireWorkerAsync/AcquireElevatedWorkerAsync + ExecuteOnWorkerAsync)
///     need RfcSessionManager.BeginContext to pin one managed thread to one
///     physical connection — but only for the duration of that one caller's
///     sequence, not for the app's whole lifetime. <see
///     cref="MaxConcurrentPinnedSessions"/>/<see cref="ElevatedWorkerCount"/>
///     are concurrency caps on these ephemeral, per-request sessions, not a
///     count of always-alive threads.
/// </summary>
public sealed class SapNcoOptions
{
    public const string SectionName = "SapNco";

    /// <summary>
    /// Max concurrent pinned sessions for non-elevated transactional
    /// sequences (AcquireWorkerAsync/ExecuteOnWorkerAsync), connected with
    /// the shared <see cref="ServiceAccount"/>. Each acquisition connects a
    /// fresh dedicated thread + SAP session on demand and tears it down on
    /// release — this bounds how many such sequences can be in flight at
    /// once, not a fixed thread pool size.
    /// </summary>
    public int MaxConcurrentPinnedSessions { get; init; } = 4;

    /// <summary>
    /// Max concurrent elevated pinned sessions — same ephemeral,
    /// connect-on-acquire/disconnect-on-release lifetime as <see
    /// cref="MaxConcurrentPinnedSessions"/>, but connected with a specific
    /// user's own SAP credentials (AcquireElevatedWorkerAsync) instead of
    /// the shared service account.
    /// </summary>
    public int ElevatedWorkerCount { get; init; } = 2;

    /// <summary>
    /// How long a caller will queue and wait for a free pinned session slot
    /// before giving up, if all <see cref="MaxConcurrentPinnedSessions"/>
    /// slots are already busy.
    /// </summary>
    public int PinnedAcquireTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How long a caller will queue and wait for a free elevated session
    /// slot before giving up, if all <see cref="ElevatedWorkerCount"/> slots
    /// are already busy with another user's elevated request.
    /// </summary>
    public int ElevatedAcquireTimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum number of queued RFC calls per pinned session before rejecting new work on it.</summary>
    public int MaxQueueDepth { get; init; } = 50;

    /// <summary>How often (seconds) the background session monitor sends a keep-alive ping to the stateless pool.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 60;

    /// <summary>Milliseconds to back off before the stateless pool's single retry after a connection failure.</summary>
    public int ReconnectDelayMs { get; init; } = 2000;

    /// <summary>
    /// SAP service-account credentials used by every pinned (non-elevated)
    /// session, and by the stateless pool when <see cref="ServiceAccounts"/>
    /// is empty/unset. Also the source of AppServerHost/SystemNumber/Client/
    /// Language for elevated-credential building at PurchasingController/
    /// PackagingController's "-elevated" endpoints — those only need the
    /// shared system connection profile, not a specific login.
    /// </summary>
    public SapConnectionOptions ServiceAccount { get; init; } = new();

    /// <summary>
    /// Optional per-account credentials for the stateless pool. When
    /// non-empty, one NCo destination is registered per entry (round-robined
    /// across calls) instead of a single destination for the shared <see
    /// cref="ServiceAccount"/>.
    /// </summary>
    public IReadOnlyList<SapConnectionOptions> ServiceAccounts { get; init; } = Array.Empty<SapConnectionOptions>();

    /// <summary>
    /// NCo's own internal RFC connection pool size for each stateless-pool
    /// destination — governs real concurrency for ordinary single-call RFCs
    /// directly, independent of any .NET thread count. Size this against
    /// your SAP system's concurrent-user license, not CPU count.
    /// </summary>
    public int PoolSize { get; init; } = 10;

    /// <summary>Ceiling NCo will grow a stateless-pool destination's connection pool to under load.</summary>
    public int MaxPoolSize { get; init; } = 20;
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
