using System.Net;
using System.Net.Http.Json;
using Moq;
using SapServer.Models;
using SapServer.Tests.Infrastructure;
using System.Web.Http;

namespace SapServer.Tests.Controllers;

public class RfcControllerTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public RfcControllerTests(SapServerTestFactory factory)
    {
        _factory = factory;
        _factory.PoolMock.Reset();
        _factory.PermissionsMock.Reset();
    }

    [Fact]
    public async Task Execute_without_a_bearer_token_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/rfc/execute", new RfcRequest { FunctionName = "RFC_PING" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Execute_when_permission_check_fails_returns_403_FORBIDDEN()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(42, "Z_SOME_RFC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client = _factory.CreateAuthenticatedClient(userId: 42);
        var response = await client.PostAsJsonAsync("/api/rfc/execute", new RfcRequest { FunctionName = "Z_SOME_RFC" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("FORBIDDEN", body!.Error!.Code);
    }

    [Fact]
    public async Task Execute_when_permitted_calls_the_pool_and_returns_its_response()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(42, "RFC_PING", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expected = new RfcResponse { Parameters = new() { ["STATUS"] = "OK" } };
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.FunctionName == "RFC_PING"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var client = _factory.CreateAuthenticatedClient(userId: 42);
        var response = await client.PostAsJsonAsync("/api/rfc/execute", new RfcRequest { FunctionName = "RFC_PING" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RfcResponse>>();
        Assert.True(body!.Success);
        Assert.Equal("OK", body.Data!.Parameters["STATUS"]!.ToString());
    }

    [Fact]
    public async Task Status_requires_admin_or_superadmin_role()
    {
        var client = _factory.CreateAuthenticatedClient(userId: 1); // no role claim at all
        var response = await client.GetAsync("/api/rfc/status");
        // Unlike ASP.NET Core's [Authorize(Roles = ...)] (403 when authenticated
        // but missing the role), System.Web.Http's AuthorizeAttribute.IsAuthorized
        // returns false for BOTH "not authenticated" and "wrong role", and
        // HandleUnauthorizedRequest always maps that to 401 — Web API 2 has no
        // built-in distinction between the two failure modes.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_returns_the_pool_snapshot_for_an_admin()
    {
        _factory.PoolMock
            .Setup(p => p.GetPoolStatus())
            .Returns([new WorkerStatus { SlotId = 0, IsConnected = true, QueueDepth = 0, LastActivity = DateTime.UtcNow }]);

        var client = _factory.CreateAuthenticatedClient(userId: 1, role: "admin");
        var response = await client.GetAsync("/api/rfc/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<WorkerStatus>>>();
        Assert.Single(body!.Data!);
        Assert.Equal(0, body.Data![0].SlotId);
    }

    /// <summary>
    /// Guards the real wire format, not just the deserialized C# object -
    /// Web API 2's default JsonMediaTypeFormatter serializes ApiResponse&lt;T&gt;'s
    /// PascalCase C# property names as-is unless a camelCase contract
    /// resolver is configured (see Startup.ConfigurePipeline). Every other
    /// test in this class deserializes the response back into
    /// ApiResponse&lt;T&gt; via ReadFromJsonAsync, which matches property names
    /// case-insensitively and so stayed green even when the live wire format
    /// was PascalCase ({"Success":true,...} instead of {"success":true,...})
    /// - confirmed for real against a live IIS deploy, where every external
    /// caller checking response.success/.data/.error (lowercase, per the
    /// documented envelope) silently treated every successful response as a
    /// failure. Only a raw string check on the actual response body catches
    /// this class of regression.
    /// </summary>
    [Fact]
    public async Task Status_response_body_uses_camelCase_property_names()
    {
        _factory.PoolMock
            .Setup(p => p.GetPoolStatus())
            .Returns([]);

        var client = _factory.CreateAuthenticatedClient(userId: 1, role: "admin");
        var response = await client.GetAsync("/api/rfc/status");

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":", raw);
        Assert.DoesNotContain("\"Success\":", raw);
    }
}
