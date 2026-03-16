using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Checks whether a portal user has been granted permission to execute a
/// specific RFC function, by querying the SapDepartmentPermissions table in
/// the same SQL Server database used by sql2005-bridge.
///
/// Results are cached per <see cref="AuthOptions.PermissionCacheSeconds"/> to
/// avoid a SQL round-trip on every request. The cache is intentionally short
/// so that permission revocations propagate quickly.
///
/// Fails closed — any database error returns false rather than allowing access.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly AuthOptions _auth;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PermissionService> _logger;

    public PermissionService(
        IOptions<AuthOptions> auth,
        IMemoryCache cache,
        ILogger<PermissionService> logger)
    {
        _auth   = auth.Value;
        _cache  = cache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> CanExecuteAsync(
        int userId,
        string functionName,
        CancellationToken ct = default)
    {
        var cacheKey = $"sap_perm|{userId}|{functionName}";

        if (_cache.TryGetValue(cacheKey, out bool cached))
            return cached;

        bool result = await QueryPermissionAsync(userId, functionName, ct);

        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_auth.PermissionCacheSeconds));

        return result;
    }

    private async Task<bool> QueryPermissionAsync(
        int userId,
        string functionName,
        CancellationToken ct)
    {
        // Language-level explanation of the query:
        //
        // A user is allowed to call an RFC function when:
        //   • Their account is active and not locked.
        //   • At least one of their portal departments maps to that RFC function
        //     in SapDepartmentPermissions.
        //
        // The wildcard function name '*' grants access to every function for
        // that department (useful for admin / superadmin rows).

        const string sql = """
            SELECT TOP 1 1
            FROM       dbo.PortalUsers               u
            INNER JOIN dbo.PortalUserDepartments     pud ON pud.UserID     = u.UserID
            INNER JOIN dbo.SapDepartmentPermissions  sdp ON sdp.Department = pud.Department
            WHERE u.UserID   = @userId
              AND u.IsActive = 1
              AND u.IsLocked = 0
              AND (sdp.FunctionName = @fn OR sdp.FunctionName = '*')
            """;

        try
        {
            await using var conn = new SqlConnection(_auth.SqlConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value    = userId;
            cmd.Parameters.Add("@fn",     System.Data.SqlDbType.NVarChar, 100).Value = functionName;

            var scalar = await cmd.ExecuteScalarAsync(ct);
            return scalar is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Permission check failed for userId={UserId}, function={Fn}. Denying access.",
                userId, functionName);
            return false; // Fail closed
        }
    }
}
