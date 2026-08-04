using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class QualityControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly QualityController _controller;

    public QualityControllerTests()
    {
        _controller = new QualityController(_pool.Object, _permissions.Object, NullLogger<QualityController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static QualityMb1bRequest SampleRequest(string storageLocation) => new()
    {
        Material = "30005R",
        Quantity = 10,
        Header = "Quality block",
        StorageLocation = storageLocation,
        BinType = "999",
        Bin = "BIN-001",
        Username = "j.smith",
    };

    [Fact]
    public async Task GetBlockedStock_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, QualityHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetBlockedStock(new StockQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task BlockStock_at_a_non_WHM_location_skips_bin_checks_and_transfer_orders_entirely()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.BlockStock(SampleRequest("3012"), CancellationToken.None); // not 1710/1711

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the MB1B call
    }

    [Fact]
    public async Task BlockStock_at_a_WHM_location_checks_both_destination_bins_then_creates_both_transfer_orders()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()) // MB1B
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "h" }, new() { ["WA"] = "BIN-A" } } } }) // bin check 1: exists
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "h" }, new() { ["WA"] = "BIN-B" } } } }) // bin check 2: exists
            .ReturnsAsync(new RfcResponse()) // transfer order 1
            .ReturnsAsync(new RfcResponse()); // transfer order 2

        var result = await _controller.BlockStock(SampleRequest("1710"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [Fact]
    public async Task BlockStock_at_a_WHM_location_fails_fast_with_422_when_a_destination_bin_is_missing()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()) // MB1B
            .ReturnsAsync(new RfcResponse()); // bin check 1: no data_display at all -> does not exist

        var result = await _controller.BlockStock(SampleRequest("1711"), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.IsType<ApiResponse<QualityMb1bResponse>>(unprocessable.Value);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // never reaches the transfer orders
    }

    [Fact]
    public async Task UnblockStock_at_a_non_WHM_location_also_skips_bin_checks()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.UnblockStock(SampleRequest("3012"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RfcResponse Mb1bMessage(string type, string message) => new()
    {
        Parameters = new() { ["MESSG"] = $"{type}    M7   001 {message}" },
    };

    // Regression test for the same bug class already fixed on the warehouse
    // consignment MB1B path: QualityController previously returned 200/
    // success:true regardless of whether the MB11 block actually posted in
    // SAP, so a rejected block (deficit stock, etc.) looked identical to a
    // successful one — the stock never actually moved even though the
    // endpoint reported success.
    [Fact]
    public async Task BlockStock_at_a_non_WHM_location_returns_422_when_SAP_rejects_the_MB11_leg()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mb1bMessage("E", "Deficit of SL stock 5 PC : 30005R 1000 999 BIN-001"));

        var result = await _controller.BlockStock(SampleRequest("3012"), CancellationToken.None); // not 1710/1711

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<QualityMb1bResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        Assert.False(body.Data!.Success);
        Assert.Contains("Deficit of SL stock", body.Error!.Message);
    }

    [Fact]
    public async Task BlockStock_at_a_WHM_location_returns_422_when_a_transfer_order_legs_RETURN_table_has_a_blocking_error()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mb1bMessage("S", "MB11 posted")) // MB1B
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "h" }, new() { ["WA"] = "BIN-A" } } } }) // bin check 1: exists
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "h" }, new() { ["WA"] = "BIN-B" } } } }) // bin check 2: exists
            .ReturnsAsync(new RfcResponse { Tables = new() { ["RETURN"] = new() { new() { ["TYPE"] = "E", ["MESSAGE"] = "Bin is full" } } } }) // transfer order 1: rejected
            .ReturnsAsync(new RfcResponse()); // transfer order 2

        var result = await _controller.BlockStock(SampleRequest("1710"), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }
}
