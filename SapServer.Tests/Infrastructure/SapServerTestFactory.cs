using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using SapServer.Services.Interfaces;

namespace SapServer.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for controller-level tests. Never lets the real
/// SapConnectionPool or PermissionService construct (the former would spin up
/// real STA threads trying to reach a real SAP system; the latter needs a
/// real SQL Server) — both are replaced with Moq mocks the test configures
/// per-scenario. Also supplies the "Auth" config section Program.cs reads
/// eagerly (and throws on if entirely missing), since there's no real
/// appsettings.json checked in — see appsettings.example.json.
/// </summary>
public sealed class SapServerTestFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "test-only-jwt-secret-at-least-32-characters-long";
    private const string JwtIssuer = "sql2005-bridge";
    private const string JwtAudience = "sap-server";

    public Mock<ISapConnectionPool> PoolMock { get; } = new();
    public Mock<IPermissionService> PermissionsMock { get; } = new();

    // Program.cs reads AuthOptions eagerly — via builder.Configuration,
    // BEFORE builder.Build() — as plain top-level statements, not inside a
    // ConfigureServices/ConfigureAppConfiguration callback. WebApplicationFactory's
    // ConfigureWebHost hook only affects the host from around .Build() onward,
    // which is too late for that read. Environment variables, by contrast, are
    // already loaded into builder.Configuration by WebApplicationBuilder.CreateBuilder(args)
    // itself, the very first line of Program.cs — so they're the one config
    // source that's actually visible in time. Static constructor: runs once,
    // before any test in this class creates the factory.
    static SapServerTestFactory()
    {
        Environment.SetEnvironmentVariable("Auth__JwtSecret", JwtSecret);
        Environment.SetEnvironmentVariable("Auth__JwtIssuer", JwtIssuer);
        Environment.SetEnvironmentVariable("Auth__JwtAudience", JwtAudience);
        Environment.SetEnvironmentVariable("Auth__SqlConnectionString", "Server=unused;Database=unused;");
        Environment.SetEnvironmentVariable("Auth__PermissionCacheSeconds", "60");
        Environment.SetEnvironmentVariable("Auth__DevBypassAuth", "false");
        Environment.SetEnvironmentVariable("Auth__BypassPermissions", "false");
        Environment.SetEnvironmentVariable("SapPool__ServiceWorkerCount", "0");
        Environment.SetEnvironmentVariable("SapPool__ElevatedWorkerCount", "0");
        Environment.SetEnvironmentVariable("AllowedOrigins__0", "https://test-frontend.invalid");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production"); // avoid Program.cs's DevBypassAuth path — we want the real JWT/permission pipeline under test

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISapConnectionPool>();
            services.AddSingleton(PoolMock.Object);

            services.RemoveAll<IPermissionService>();
            services.AddSingleton(PermissionsMock.Object);

            // SapSessionMonitor (a hosted BackgroundService) would otherwise
            // start pinging the mocked pool on a timer for the life of the
            // test host — harmless against a mock, but pure noise.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        });
    }

    /// <summary>Builds a JWT the running test host will accept, matching the claim shape
    /// sql2005-bridge issues: a "userId" claim, plus optional role claims for
    /// [Authorize(Roles = "...")]-gated endpoints like RfcController.Status.</summary>
    public string CreateToken(int userId, string? role = null)
    {
        var claims = new List<Claim> { new("userId", userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient CreateAuthenticatedClient(int userId, string? role = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateToken(userId, role));
        return client;
    }
}
