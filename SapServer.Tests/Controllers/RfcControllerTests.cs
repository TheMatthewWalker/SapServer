using System.Net;
using System.Net.Http.Json;
using Moq;
using SapServer.Models;
using SapServer.Tests.Infrastructure;

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
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
}
