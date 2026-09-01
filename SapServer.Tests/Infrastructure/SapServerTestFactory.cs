using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Owin.Testing;
using Moq;
using SapServer.Services.Interfaces;

namespace SapServer.Tests.Infrastructure;

/// <summary>
/// OWIN TestServer for controller-level tests — the net48/OWIN replacement
/// for WebApplicationFactory&lt;Program&gt;, which has no equivalent outside
/// ASP.NET Core's generic host. Drives the exact same pipeline production
/// uses (Startup.ConfigurePipeline: JWT auth, CORS, exception handling, Web
/// API routing) rather than duplicating it, so this test still exercises the
/// real auth/permission pipeline end to end — only ISapConnectionPool and
/// IPermissionService are swapped for Moq mocks (via ConfigurePipeline's
/// overrideServices hook), same as WebApplicationFactory's
/// ConfigureWebHost + RemoveAll&lt;T&gt;() did before. SapSessionMonitor's
/// Timer is skipped (startSessionMonitor: false) — it would otherwise ping
/// the mocked pool for the life of the test host, harmless but pure noise.
/// </summary>
public sealed class SapServerTestFactory : IDisposable
{
    public const string JwtSecret = "test-only-jwt-secret-at-least-32-characters-long";
    private const string JwtIssuer = "normanton-nexus";
    private const string JwtAudience = "sap-server";

    public Mock<ISapConnectionPool> PoolMock { get; } = new();
    public Mock<IPermissionService> PermissionsMock { get; } = new();

    private readonly TestServer _server;

    public SapServerTestFactory()
    {
        var configuration = BuildTestConfiguration();

        _server = TestServer.Create(app =>
        {
            Startup.ConfigurePipeline(
                app,
                configuration,
                overrideServices: services =>
                {
                    services.AddSingleton(PoolMock.Object);
                    services.AddSingleton(PermissionsMock.Object);
                },
                startSessionMonitor: false);
        });
    }

    private static IConfiguration BuildTestConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:JwtSecret"] = JwtSecret,
                ["Auth:JwtIssuer"] = JwtIssuer,
                ["Auth:JwtAudience"] = JwtAudience,
                ["Auth:SqlConnectionString"] = "Server=unused;Database=unused;",
                ["Auth:PermissionCacheSeconds"] = "60",
                ["Auth:DevBypassAuth"] = "false",
                ["Auth:BypassPermissions"] = "false",
                ["SapNco:MaxConcurrentPinnedSessions"] = "0",
                ["SapNco:ElevatedWorkerCount"] = "0",
                ["AllowedOrigins:0"] = "https://test-frontend.invalid",
            })
            .Build();

    /// <summary>
    /// A fresh HttpClient wrapping the same in-memory OWIN pipeline each
    /// call — mirrors WebApplicationFactory.CreateClient()'s per-call
    /// isolation (TestServer.HttpClient itself is one shared instance, which
    /// would make DefaultRequestHeaders mutations from CreateAuthenticatedClient
    /// leak across tests sharing this factory via IClassFixture).
    /// </summary>
    public HttpClient CreateClient() => new(_server.Handler) { BaseAddress = _server.BaseAddress };

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
            new AuthenticationHeaderValue("Bearer", CreateToken(userId, role));
        return client;
    }

    public void Dispose() => _server.Dispose();
}
