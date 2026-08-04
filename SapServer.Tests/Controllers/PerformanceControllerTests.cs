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

public class PerformanceControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly PerformanceController _controller;

    private static RfcResponse SingleValue(string wa) => new()
    {
        Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = wa } } },
    };

    public PerformanceControllerTests()
    {
        _controller = new PerformanceController(_pool.Object, _permissions.Object, NullLogger<PerformanceController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task GetStock_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetStock(CancellationToken.None));
    }

    [Fact]
    public async Task GetStock_queries_stock_then_profit_centre_lookup()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.GetStock(CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetVbfaOrderLink_returns_the_parsed_rows()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.GetVbfaOrderLink("0080001234", CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetValuationClasses_returns_the_parsed_catalog()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.GetValuationClasses(CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTurnsValClass_skips_history_movement_and_consignment_lookups_when_no_materials_come_back()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()) // material master: no rows -> no materials
            .ReturnsAsync(new RfcResponse()); // demand forecast

        var result = await _controller.GetTurnsValClass(new TurnsValClassQuery(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // master + forecast only
    }

    [Fact]
    public async Task ChangeValuationClass_rejects_a_request_with_no_order_before_touching_the_pool()
    {
        var result = await _controller.ChangeValuationClass(new ChangeValuationClassRequest { Order = "", Changes = [] }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeValuationClass_rejects_a_request_with_no_changes()
    {
        var result = await _controller.ChangeValuationClass(
            new ChangeValuationClassRequest { Order = "000001234500", Changes = [] }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeValuationClass_stops_early_with_422_when_the_order_belongs_to_a_different_company_code()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleValue("0312")) // plant's company code
            .ReturnsAsync(SingleValue("0999")); // order's company code — different

        var result = await _controller.ChangeValuationClass(new ChangeValuationClassRequest
        {
            Order = "000001234500",
            Changes = [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3000" }],
        }, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<ChangeValuationClassResponse>>(unprocessable.Value);
        Assert.False(body.Data!.Success);
        Assert.Contains("does not exist in company code", body.Error!.Message);
        // Only the two company-code lookups — never reaches the material master / stock / MB1A steps.
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
