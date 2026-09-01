using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;
using System.Web.Http;

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

        ControllerTestHelpers.AssertOk(result);
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

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<FunctionParams[]>>(ok);
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

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetVendorGr_merges_movement_101_and_102_results()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetVendorGr("0000100123", null, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetConsignmentStock_requires_no_permission_check_at_all()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetConsignmentStock(CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _permissions.Verify(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class MrpAnalysisControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly MrpAnalysisController _controller;

    public MrpAnalysisControllerTests()
    {
        _controller = new MrpAnalysisController(_pool.Object, _permissions.Object, NullLogger<MrpAnalysisController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
    }

    [Fact]
    public async Task GetConsumptionByYear_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetConsumptionByYear(CancellationToken.None));
    }

    [Fact]
    public async Task GetConsumptionByYear_returns_the_parsed_rows()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "MATNR|WERKS|GJAHR|GSV01|GSV02|GSV03|GSV04|GSV05|GSV06|GSV07|GSV08|GSV09|GSV10|GSV11|GSV12" },
                        new() { ["WA"] = "30005R|3012|2025|1|1|1|1|1|1|1|1|1|1|1|1" },
                    }
                }
            });

        var result = await _controller.GetConsumptionByYear(CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<ConsumptionByYearRow[]>>(ok);
        var row = Assert.Single(body.Data!);
        Assert.Equal("30005R", row.Material);
        Assert.Equal(2025, row.FiscalYear);
        Assert.Equal(12m, row.Qty);
    }

    [Fact]
    public async Task GetGoodsReceiptHistory_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetGoodsReceiptHistory(null, CancellationToken.None));
    }

    [Fact]
    public async Task GetGoodsReceiptHistory_joins_the_goods_receipt_pull_to_the_purchase_order_vendor_pull()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.InputTables["QUERY_TABLES"].Any(row => (string?)row["TABNAME"] == "MSEG")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "MATNR|MENGE|MEINS|EBELN|LIFNR|BUDAT" },
                        new() { ["WA"] = "30005R|100|KG|4500012345| |02.01.2026" }, // no MSEG-LIFNR — ordinary PO receipt
                    }
                }
            });
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.InputTables["QUERY_TABLES"].Any(row => (string?)row["TABNAME"] == "EKKO")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "EBELN|LIFNR" },
                        new() { ["WA"] = "4500012345|0000099999" },
                    }
                }
            });

        var result = await _controller.GetGoodsReceiptHistory(null, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<GoodsReceiptHistoryRow[]>>(ok);
        var row = Assert.Single(body.Data!);
        Assert.Equal("30005R", row.Material);
        Assert.Equal("0000099999", row.Vendor); // resolved via EKKO, not MSEG (which was blank)
        Assert.Equal(2026, row.Year);
        Assert.Equal(100m, row.Qty);
    }

    [Fact]
    public async Task ExplodeBom_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() =>
            _controller.ExplodeBom(new BomExplosionRequest { Items = [new BomExplosionItem { Material = "FG1", Quantity = 10m }] }, CancellationToken.None));
    }

    [Fact]
    public async Task ExplodeBom_wires_the_real_pool_calls_through_to_a_raw_material_total()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, MrpAnalysisHelper.FnMrpAnalysis, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.InputTables["QUERY_TABLES"].Any(row => (string?)row["TABNAME"] == "ZBOM_INFO")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "MATNR|WERKS|IDNRK|POSNR|MENGE|MEINS|LGORT|PRVBE" },
                        new() { ["WA"] = "FG1|3012|RAW1|0010|2|KG|1710|312" },
                    }
                }
            });
        _pool.Setup(p => p.ExecuteAsync(It.Is<RfcRequest>(r => r.InputTables["QUERY_TABLES"].Any(row => (string?)row["TABNAME"] == "MARC")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "MATNR|PRCTR" },
                        new() { ["WA"] = "RAW1|0000002012" },
                    }
                }
            });

        var result = await _controller.ExplodeBom(
            new BomExplosionRequest { Items = [new BomExplosionItem { Material = "FG1", Quantity = 10m }] },
            CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<BomExplosionResult>>(ok);
        var row = Assert.Single(body.Data!.RawMaterials);
        Assert.Equal("RAW1", row.Material);
        Assert.Equal(20m, row.Quantity); // 10 * 2
        Assert.Empty(body.Data!.Unresolved);
    }
}
