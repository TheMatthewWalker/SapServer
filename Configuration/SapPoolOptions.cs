namespace SapServer.Configuration;

public sealed class SapPoolOptions
{
    public const string SectionName = "SapPool";

    /// <summary>
    /// Number of STA worker threads / persistent SAP connections in the pool.
    /// Set to 0 for automatic: defaults to <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int PoolSize { get; init; } = 0;

    /// <summary>Maximum number of queued work items per worker before rejecting new requests.</summary>
    public int MaxQueueDepth { get; init; } = 50;

    /// <summary>
    /// Seconds of inactivity after which the session monitor sends an RFC_PING keep-alive.
    /// SAP GUI typically disconnects idle sessions after ~10 minutes.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 300;

    /// <summary>How often (seconds) the background session monitor runs its health check.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 60;

    /// <summary>Milliseconds to wait before retrying after a failed reconnection attempt.</summary>
    public int ReconnectDelayMs { get; init; } = 5000;

    /// <summary>SAP service-account credentials used by every pool worker.</summary>
    public SapConnectionOptions ServiceAccount { get; init; } = new();

    /// <summary>Resolved pool size — substitutes ProcessorCount when PoolSize is 0.</summary>
    public int ResolvedPoolSize => PoolSize > 0 ? PoolSize : Environment.ProcessorCount;
}

public sealed class SapConnectionOptions
{
    public string System   { get; init; } = string.Empty;
    public string Client   { get; init; } = string.Empty;
    public string SystemId { get; init; } = string.Empty;
    public string User     { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Language { get; init; } = "EN";
}
