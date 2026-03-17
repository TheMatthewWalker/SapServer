namespace SapServer.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// HMAC-SHA256 secret shared with sql2005-bridge for JWT validation.
    /// Must match the secret used by sql2005-bridge when signing tokens.
    /// </summary>
    public string JwtSecret { get; init; } = string.Empty;

    public string JwtIssuer   { get; init; } = "sql2005-bridge";
    public string JwtAudience { get; init; } = "sap-server";

    /// <summary>
    /// Connection string for the SQL Server that contains PortalUsers,
    /// PortalUserDepartments, and SapDepartmentPermissions tables.
    /// </summary>
    public string SqlConnectionString { get; init; } = string.Empty;

    /// <summary>How long (seconds) to cache permission lookups before re-querying SQL Server.</summary>
    public int PermissionCacheSeconds { get; init; } = 60;

    /// <summary>
    /// When true (Development environment only), bypasses JWT validation and
    /// permission checks so the API can be tested without sql2005-bridge.
    /// Must never be enabled in Production.
    /// </summary>
    public bool DevBypassAuth { get; init; } = false;
}
