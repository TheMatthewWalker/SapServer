using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class WarehouseControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly WarehouseController _controller;

    public WarehouseControllerTests()
    {
        _controller = new WarehouseController(_pool.Object, _permissions.Object, NullLogger<WarehouseController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
    }

    [Fact]
    public async Task GetStock_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetStock(new StockQuery(), CancellationToken.None));
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStock_returns_parsed_rows_when_permitted()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "header" },
                        new() { ["WA"] = "1710|SA|BIN-001|30005R|10|BATCH1|F|Q|SO1" },
                    },
                },
            });

        var result = Assert.IsType<OkObjectResult>(await _controller.GetStock(new StockQuery(), CancellationToken.None));
        var body = Assert.IsType<ApiResponse<StockRow[]>>(result.Value);
        Assert.Single(body.Data!);
        Assert.Equal("30005R", body.Data![0].Material);
    }

    [Fact]
    public async Task CreateTransferOrder_fails_fast_with_422_when_the_destination_bin_does_not_exist_and_never_calls_L_TO_CREATE_SINGLE()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnCreateTo, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no data_display -> BinExists() is false

        var result = await _controller.CreateTransferOrder(
            new CreateTransferOrderRequest { DestinationType = "999", DestinationBin = "BADBIN" }, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<CreateTransferOrderResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the bin check
    }

    [Fact]
    public async Task CreateTransferOrder_creates_the_TO_when_the_destination_bin_exists()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnCreateTo, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPLA" }, new() { ["WA"] = "BIN-001" } } } }) // bin check: header row + one real hit -> exists
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["E_TANUM"] = "0000001234" } }); // TO creation

        var result = await _controller.CreateTransferOrder(
            new CreateTransferOrderRequest { DestinationType = "999", DestinationBin = "BIN-001" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<CreateTransferOrderResponse>>(ok.Value);
        Assert.Equal("0000001234", body.Data!.TransferOrderNumber);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateStockAdjustment_rejects_an_unexpected_movement_type_before_touching_the_pool()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, StockAdjustmentHelper.FnGoodsMvtCreate, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.CreateStockAdjustment(new StockAdjustmentRequest { MovementType = "999" }, dryRun: false, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.AcquireWorker(), Times.Never);
    }

    [Fact]
    public async Task CreateStockAdjustment_dryRun_returns_the_built_request_without_acquiring_a_worker()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, StockAdjustmentHelper.FnGoodsMvtCreate, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.CreateStockAdjustment(
            new StockAdjustmentRequest { MovementType = "711", Material = "30005R" }, dryRun: true, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<RfcRequest>>(ok.Value);
        Assert.Equal(StockAdjustmentHelper.FnGoodsMvtCreate, body.Data!.FunctionName);
        _pool.Verify(p => p.AcquireWorker(), Times.Never);
    }

    [Fact]
    public async Task CreateStockAdjustment_commits_on_success()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, StockAdjustmentHelper.FnGoodsMvtCreate, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MATERIALDOCUMENT"] = "5000009999" } });

        var result = await _controller.CreateStockAdjustment(
            new StockAdjustmentRequest { MovementType = "711", Material = "30005R" }, dryRun: false, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Once);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateStockAdjustment_rolls_back_when_SAP_reports_no_material_document()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, StockAdjustmentHelper.FnGoodsMvtCreate, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no MATERIALDOCUMENT -> Success = false

        var result = await _controller.CreateStockAdjustment(
            new StockAdjustmentRequest { MovementType = "711", Material = "30005R" }, dryRun: false, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateStockAdjustment_always_rolls_back_a_test_run_even_when_SAP_reports_success()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, StockAdjustmentHelper.FnGoodsMvtCreate, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MATERIALDOCUMENT"] = "5000009999" } });

        var result = await _controller.CreateStockAdjustment(
            new StockAdjustmentRequest { MovementType = "711", Material = "30005R", TestRun = true }, dryRun: false, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PicksheetStock_short_circuits_on_an_empty_materials_list_without_calling_the_pool()
    {
        var result = await _controller.PicksheetStock(new PicksheetStockRequest { Materials = [] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<PicksheetBatchRow[]>>(ok.Value);
        Assert.Empty(body.Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateLt04_fails_fast_with_422_when_the_batch_is_quality_blocked()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = "Q" } } } });

        var result = await _controller.CreateLt04(new CreateLt04Request { Material = "30005R", PalletOrBatch = "BATCH1" }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the quality check
    }

    private static RfcResponse Mb1bMessage(string type, string message) => new()
    {
        Parameters = new() { ["MESSG"] = $"{type}    M7   001 {message}" },
    };

    private static ConsignmentMb1bRequest SampleConsignmentBody() => new()
    {
        Material = "30005R",
        Quantity = 5,
        Header = "Test",
        SpecialStockNumber = "12345",
        StorageLocation = "1000",
        SourceType = "PDR",
        SourceBin = "B01",
        DestinationType = "SA",
        DestinationBin = "B02",
    };

    [Fact]
    public async Task ConsignmentMb1b_returns_200_when_all_three_legs_succeed()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mb1bMessage("S", "MB1B posted"))
            .ReturnsAsync(Mb1bMessage("S", "Moved to non-consign"))
            .ReturnsAsync(Mb1bMessage("S", "Moved to consign"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<ConsignmentMb1bResponse>>(ok.Value);
        Assert.True(body.Data!.Success);
    }

    // Regression test for the Staging Post bug: SapServer previously always
    // returned 200/success:true for consignment-mb1b regardless of whether
    // MB1B actually posted in SAP, so a rejected goods movement (deficit
    // stock, missing authorization, etc.) looked identical to a real one to
    // every caller (routes/staging.js's Mark Delivered flow chief among
    // them) — the consignment stock never actually left SAP even though the
    // portal recorded the delivery as successful.
    [Fact]
    public async Task ConsignmentMb1b_returns_422_when_the_MB1B_leg_is_rejected_by_SAP()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mb1bMessage("E", "Deficit of SL stock 5 PC : 30005R 1000 SA B02"))
            .ReturnsAsync(Mb1bMessage("S", "Moved to non-consign"))
            .ReturnsAsync(Mb1bMessage("S", "Moved to consign"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<ConsignmentMb1bResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        Assert.False(body.Data!.Success);
        Assert.Contains("Deficit of SL stock", body.Error!.Message);
    }

    [Fact]
    public async Task ConsignmentMb1b_returns_422_when_either_LT01_leg_is_rejected_by_SAP()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mb1bMessage("S", "MB1B posted"))
            .ReturnsAsync(Mb1bMessage("S", "Moved to non-consign"))
            .ReturnsAsync(Mb1bMessage("E", "Bin does not exist"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task GetZdelflagLikpAblad_requires_no_permission_check_at_all()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetZdelflagLikpAblad("0080001234", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _permissions.Verify(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
