namespace SapServer.Configuration;

/// <summary>
/// Configuration for the SAP NCo rebuild spike (Services/Nco/*) — separate and
/// parallel to <see cref="SapPoolOptions"/>, which still drives the real
/// SAPFunctions64 COM pool that the app actually runs on. See CLAUDE.md's
/// "SAP NCo Spike" section.
/// </summary>
public sealed class SapNcoOptions
{
    public const string SectionName = "SapNco";

    /// <summary>
    /// Logical destination name passed to RfcDestinationManager.GetDestination.
    /// Arbitrary — NcoDestinationConfiguration ignores the name and always
    /// returns this same options object's connection parameters, since this
    /// spike only ever talks to one SAP system.
    /// </summary>
    public string DestinationName { get; init; } = "SAP_NCO_SPIKE";

    public string AppServerHost { get; init; } = string.Empty;
    public string SystemNumber { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Language { get; init; } = "EN";

    /// <summary>
    /// NCo's own internal RFC connection pool size for this destination — NOT
    /// a thread count. Unlike SapPoolOptions.ServiceWorkerCount (one dedicated
    /// STA thread per slot), NCo destinations are thread-safe and pool actual
    /// RFC connections internally; this just tells NCo how many to keep warm.
    /// </summary>
    public int PoolSize { get; init; } = 4;

    /// <summary>Ceiling NCo will grow the connection pool to under load.</summary>
    public int MaxPoolSize { get; init; } = 8;
}
