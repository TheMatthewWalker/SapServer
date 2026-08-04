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

public class LogisticsControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly LogisticsController _controller;

    public LogisticsControllerTests()
    {
        _controller = new LogisticsController(_pool.Object, _permissions.Object, NullLogger<LogisticsController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
    }

    [Fact]
    public async Task GetOpenPicksheets_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, LogisticsHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetOpenPicksheets(CancellationToken.None));
    }

    [Fact]
    public async Task GetOpenPicksheets_queries_VBUK_then_the_picksheet_detail_in_sequence()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, LogisticsHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetOpenPicksheets(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}

public class FunctionControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly FunctionController _controller;

    public FunctionControllerTests()
    {
        _controller = new FunctionController(_pool.Object, _permissions.Object, NullLogger<FunctionController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task GetFunctionParams_only_looks_up_fields_for_parameters_that_have_a_structure_type()
    {
        // Two params come back: one with a TABNAME (structure), one without —
        // only the first should trigger a BuildFunctionFields lookup.
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.FunctionName == FunctionHelper.FnGetFunction), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["PARAMS"] = new()
                    {
                        new() { ["PARAMETER"] = "I_MATNR", ["PARAMCLASS"] = "I", ["TABNAME"] = "" },
                        new() { ["PARAMETER"] = "T_RETURN", ["PARAMCLASS"] = "T", ["TABNAME"] = "BAPIRET2" },
                    },
                },
            });
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.FunctionName == FunctionHelper.FnGetFunctionFields), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetFunctionParams(new FunctionQuery { FunctionName = "Z_SOME_RFC" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<FunctionParams[]>>(ok.Value);
        Assert.Equal(2, body.Data!.Length);
        _pool.Verify(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.FunctionName == FunctionHelper.FnGetFunctionFields), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class ConsignmentControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly ConsignmentController _controller;

    public ConsignmentControllerTests()
    {
        _controller = new ConsignmentController(_pool.Object, _permissions.Object, NullLogger<ConsignmentController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
    }

    [Fact]
    public async Task GetVendorGr_requires_a_vendor_number_before_calling_the_pool()
    {
        var result = await _controller.GetVendorGr(sapVendorNumber: "", sinceDate: null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetVendorGr_merges_movement_101_and_102_results()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetVendorGr("0000100123", null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetConsignmentStock_requires_no_permission_check_at_all()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetConsignmentStock(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _permissions.Verify(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
