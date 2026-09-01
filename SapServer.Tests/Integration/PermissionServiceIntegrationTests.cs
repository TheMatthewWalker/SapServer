using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Services;
using SapServer.Tests.Infrastructure;
using Xunit;

namespace SapServer.Tests.Integration;

/// <summary>
/// Runs the REAL PermissionService against the real staging SQL Server — it
/// opens its own SqlConnection directly (not through an injectable
/// abstraction), so unlike the mocked controller tests, this is the only way
/// to verify its query/caching/fail-closed behavior actually holds against
/// the migrated database. Manages its own fixture rows (PortalUsers,
/// PortalUserDepartments, SapDepartmentPermissions) rather than depending on
/// anything pre-seeded on staging. Skips (not fails) unless
/// TEST_SQL_SERVER/TEST_SQL_DATABASE are set — see StagingDb.
/// </summary>
public class PermissionServiceIntegrationTests : IAsyncLifetime
{
    private const string TestFunctionName = "Z_JEST_INTEGRATION_TEST_FN";
    private const string TestDepartment = "jest_integration_test_dept";
    private int _activeUserId;
    private int _lockedUserId;
    private int _inactiveUserId;
    private int _wildcardUserId;
    private int _noPermissionUserId;

    public async Task InitializeAsync()
    {
        if (!StagingDb.IsConfigured) return;

        using var conn = new SqlConnection(StagingDb.ConnectionString());
        await conn.OpenAsync();

        _activeUserId = await InsertUser(conn, "jest.perm.active", isActive: true, isLocked: false);
        _lockedUserId = await InsertUser(conn, "jest.perm.locked", isActive: true, isLocked: true);
        _inactiveUserId = await InsertUser(conn, "jest.perm.inactive", isActive: false, isLocked: false);
        _wildcardUserId = await InsertUser(conn, "jest.perm.wildcard", isActive: true, isLocked: false);
        _noPermissionUserId = await InsertUser(conn, "jest.perm.none", isActive: true, isLocked: false);

        await InsertDepartment(conn, _activeUserId, TestDepartment);
        await InsertDepartment(conn, _lockedUserId, TestDepartment);
        await InsertDepartment(conn, _inactiveUserId, TestDepartment);
        await InsertDepartment(conn, _wildcardUserId, TestDepartment + "_wildcard");
        // _noPermissionUserId deliberately gets no department at all.

        using (var cmd = new SqlCommand(
            "INSERT INTO dbo.SapDepartmentPermissions (Department, FunctionName, GrantedBy) VALUES (@dept, @fn, 'jest')", conn))
        {
            cmd.Parameters.AddWithValue("@dept", TestDepartment);
            cmd.Parameters.AddWithValue("@fn", TestFunctionName);
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = new SqlCommand(
            "INSERT INTO dbo.SapDepartmentPermissions (Department, FunctionName, GrantedBy) VALUES (@dept, '*', 'jest')", conn))
        {
            cmd.Parameters.AddWithValue("@dept", TestDepartment + "_wildcard");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (!StagingDb.IsConfigured) return;

        using var conn = new SqlConnection(StagingDb.ConnectionString());
        await conn.OpenAsync();

        using var cmd = new SqlCommand(@"
            DELETE FROM dbo.SapDepartmentPermissions WHERE Department LIKE @deptPrefix + '%';
            DELETE FROM dbo.PortalUserDepartments WHERE Department LIKE @deptPrefix + '%';
            DELETE FROM dbo.PortalUsers WHERE Username LIKE 'jest.perm.%';", conn);
        cmd.Parameters.AddWithValue("@deptPrefix", TestDepartment);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertUser(SqlConnection conn, string username, bool isActive, bool isLocked)
    {
        using var cmd = new SqlCommand(@"
            INSERT INTO dbo.PortalUsers (Username, FirstName, LastName, Email, PasswordHash, Role, IsActive, IsLocked)
            OUTPUT INSERTED.UserID
            VALUES (@u, 'Jest', 'Integration', @email, 'unused-hash', 'operator', @active, @locked)", conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@email", $"{username}@example.invalid");
        cmd.Parameters.AddWithValue("@active", isActive);
        cmd.Parameters.AddWithValue("@locked", isLocked);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertDepartment(SqlConnection conn, int userId, string department)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.PortalUserDepartments (UserID, Department) VALUES (@u, @d)", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@d", department);
        await cmd.ExecuteNonQueryAsync();
    }

    private static PermissionService BuildService(string connectionString) =>
        new(Options.Create(new AuthOptions { SqlConnectionString = connectionString, PermissionCacheSeconds = 60 }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PermissionService>.Instance);

    [SkippableFact]
    public async Task Grants_access_for_an_active_user_whose_department_has_the_exact_function()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService(StagingDb.ConnectionString());
        Assert.True(await service.CanExecuteAsync(_activeUserId, TestFunctionName));
    }

    [SkippableFact]
    public async Task Denies_access_for_a_locked_user_even_with_the_right_department()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService(StagingDb.ConnectionString());
        Assert.False(await service.CanExecuteAsync(_lockedUserId, TestFunctionName));
    }

    [SkippableFact]
    public async Task Denies_access_for_an_inactive_user_even_with_the_right_department()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService(StagingDb.ConnectionString());
        Assert.False(await service.CanExecuteAsync(_inactiveUserId, TestFunctionName));
    }

    [SkippableFact]
    public async Task A_wildcard_department_permission_grants_any_function_name()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService(StagingDb.ConnectionString());
        Assert.True(await service.CanExecuteAsync(_wildcardUserId, "ANY_RANDOM_FUNCTION_NAME"));
    }

    [SkippableFact]
    public async Task Denies_access_when_the_user_has_no_department_granting_that_function()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService(StagingDb.ConnectionString());
        Assert.False(await service.CanExecuteAsync(_noPermissionUserId, TestFunctionName));
    }

    [SkippableFact]
    public async Task Fails_closed_denying_access_when_the_connection_string_is_invalid()
    {
        Skip.IfNot(StagingDb.IsConfigured, "TEST_SQL_SERVER/TEST_SQL_DATABASE not set");

        var service = BuildService("Server=this-host-does-not-exist.invalid;Database=nope;Connect Timeout=2;");
        Assert.False(await service.CanExecuteAsync(_activeUserId, TestFunctionName));
    }
}
