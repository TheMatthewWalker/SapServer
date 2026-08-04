using System.Net;
using System.Net.Http.Json;
using Moq;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class ProductionControllerTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public ProductionControllerTests(SapServerTestFactory factory)
    {
        _factory = factory;
        _factory.PoolMock.Reset();
        _factory.PermissionsMock.Reset();
    }

    [Fact]
    public async Task FindBackflushDocument_without_the_required_permission_returns_403()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.PoolMock.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FindBackflushDocument_returns_400_when_no_movement_131_row_is_found()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no data_display table at all

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FindBackflushDocument_returns_the_parsed_row_when_permitted_and_found()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sapResponse = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MBLNR|MATNR|MENGE|LGORT" }, // header — skipped
                    new() { ["WA"] = "5000001234|30005R|10.0|1710" },
                },
            },
        };
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sapResponse);

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BackflushDocumentRow>>();
        Assert.Equal("5000001234", body!.Data!.MaterialDocument);
        Assert.Equal("30005R", body.Data.Material);
        Assert.Equal("1710", body.Data.StorageLocation);
    }
}
