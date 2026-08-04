using Microsoft.Data.SqlClient;

namespace SapServer.Tests.Infrastructure;

/// <summary>
/// Shared helper for integration tests that hit the real staging SQL Server
/// (the new, migrated version) instead of mocking. Mirrors
/// Normanton-Nexus/test/helpers/stagingDb.js's env var convention exactly
/// (TEST_SQL_SERVER/TEST_SQL_DATABASE/TEST_SQL_USER/TEST_SQL_PASSWORD) so
/// both stacks' integration suites point at the same staging instance from
/// the same set of env vars.
/// </summary>
public static class StagingDb
{
    public static bool IsConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_SQL_SERVER"))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_SQL_DATABASE"));

    public static string ConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("TEST_SQL_SERVER"),
            InitialCatalog = Environment.GetEnvironmentVariable("TEST_SQL_DATABASE"),
            UserID = Environment.GetEnvironmentVariable("TEST_SQL_USER"),
            Password = Environment.GetEnvironmentVariable("TEST_SQL_PASSWORD"),
            TrustServerCertificate = true,
            Encrypt = Environment.GetEnvironmentVariable("TEST_SQL_ENCRYPT") != "false",
        };
        return builder.ConnectionString;
    }
}
