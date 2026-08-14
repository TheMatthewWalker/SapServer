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
                        new() { ["WA"] = "1710|SA|BIN-001|30005R|10|BATCH1|F|Q|SO1|20260110" },
                    },
                },
            });

        var result = Assert.IsType<OkObjectResult>(await _controller.GetStock(new StockQuery(), CancellationToken.None));
        var body = Assert.IsType<ApiResponse<StockRow[]>>(result.Value);
        Assert.Single(body.Data!);
        Assert.Equal("30005R", body.Data![0].Material);
        Assert.Equal("20260110", body.Data![0].GrDate);
    }

    [Fact]
    public async Task GetStock_skips_the_MARC_ProfitCentre_lookup_entirely_when_not_filtering_by_it()
    {
        // The MARC pull is an expensive, unfiltered whole-plant read — only
        // worth paying for when a caller actually needs to filter by Profit
        // Centre. An ordinary search should hit SAP once, not twice, and
        // ProfitCentre comes back blank rather than silently doing the join anyway.
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "header" },
                        new() { ["WA"] = "1710|SA|BIN-001|30005R|10|BATCH1|F|Q|SO1|20260110" },
                    },
                },
            });

        var result = Assert.IsType<OkObjectResult>(await _controller.GetStock(new StockQuery(), CancellationToken.None));
        var body = Assert.IsType<ApiResponse<StockRow[]>>(result.Value);

        Assert.Equal("", body.Data![0].ProfitCentre);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStock_joins_in_ProfitCentre_from_the_MARC_lookup_and_filters_by_it_when_requested()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var stockResponse = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "1710|SA|BIN-001|30005R|10|BATCH1|F|Q|SO1|20260110" },
                    new() { ["WA"] = "1710|SA|BIN-002|30006R|5|BATCH2|F|Q|SO2|20260110" },
                },
            },
        };
        var pcResponse = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "30005R|9912" },
                    new() { ["WA"] = "30006R|9913" },
                },
            },
        };
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stockResponse)
            .ReturnsAsync(pcResponse);

        var result = Assert.IsType<OkObjectResult>(await _controller.GetStock(new StockQuery { ProfitCentre = "9912" }, CancellationToken.None));
        var body = Assert.IsType<ApiResponse<StockRow[]>>(result.Value);

        Assert.Single(body.Data!);
        Assert.Equal("30005R", body.Data![0].Material);
        Assert.Equal("9912", body.Data![0].ProfitCentre);
    }

    [Fact]
    public async Task GetImStock_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.GetImStock(new ImStockQuery { StorageLocation = "1716" }, CancellationToken.None));
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetImStock_returns_parsed_MARD_rows_when_permitted()
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
                        new() { ["WA"] = "3012|1716|30005R|42.5" },
                    },
                },
            });

        var result = Assert.IsType<OkObjectResult>(await _controller.GetImStock(new ImStockQuery { StorageLocation = "1716" }, CancellationToken.None));
        var body = Assert.IsType<ApiResponse<ImStockRow[]>>(result.Value);
        Assert.Single(body.Data!);
        Assert.Equal("30005R", body.Data![0].Material);
        Assert.Equal(42.5m, body.Data![0].AvailableQty);
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

    // ── Bin → storage type lookup ────────────────────────────────────────────

    [Fact]
    public async Task GetBinStorageTypes_returns_empty_array_without_calling_SAP_for_a_blank_bin()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.GetBinStorageTypes("", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<string[]>>(ok.Value);
        Assert.Empty(body.Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBinStorageTypes_pads_a_numeric_bin_before_querying_LAGP()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = "SA" } } } });

        var result = await _controller.GetBinStorageTypes("123", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<string[]>>(ok.Value);
        Assert.Equal(["SA"], body.Data!);
        _pool.Verify(p => p.ExecuteAsync(
            It.Is<RfcRequest>(r => r.InputTablesItems["where_clause"].Any(row => ((string)row["TEXT"]!).Contains("0000000123"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Delete TR ────────────────────────────────────────────────────────────

    private static RfcResponse BdcMessage(string type, string msgClass, string number, string message) => new()
    {
        Parameters = new() { ["MESSG"] = $"{type} {msgClass} {number} {message}" },
    };

    private static RfcResponse ExistsCheckResponse(bool trStillExists) => trStillExists
        ? new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "TBNUM" }, new() { ["WA"] = "4500001234" } } } }
        : new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "TBNUM" } } } }; // header only -> gone

    [Fact]
    public async Task DeleteTr_returns_200_when_the_delete_succeeds_and_the_exists_check_confirms_the_TR_is_gone()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BdcMessage("S", "L2", "018", "Transfer requirement deleted"))
            .ReturnsAsync(ExistsCheckResponse(trStillExists: false));

        var result = await _controller.DeleteTr(new DeleteTrRequest { TrNumber = "4500001234" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // delete + verify
    }

    [Fact]
    public async Task DeleteTr_returns_422_immediately_when_blocked_on_item_1_without_attempting_a_verification_or_retry()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BdcMessage("E", "L2", "019", "You are not allowed to delete transfer requirement item 0001"));

        var result = await _controller.DeleteTr(new DeleteTrRequest { TrNumber = "4500001234" }, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<BdcResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        // Only the primary delete attempt — no unverified fallback BDC and no
        // exists-check, since the refusal is already a definitive answer.
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Regression test for the exact bug reported in production: SAP's BDC
    // processor reported Type "S" for "Field LTBP1-LVORM does not exist in
    // dynpro SAPML02B 0102" (a message-class "00" framework failure, not a
    // real business result) even though nothing was deleted — the endpoint
    // returned success:true for a TR that was never actually removed.
    [Fact]
    public async Task DeleteTr_returns_422_when_SAP_reports_a_non_blocked_S_type_message_but_the_TR_still_exists()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BdcMessage("S", "00", "349", "Field LTBP1-LVORM does not exist in dynpro SAPML02B 0102"))
            .ReturnsAsync(ExistsCheckResponse(trStillExists: true));

        var result = await _controller.DeleteTr(new DeleteTrRequest { TrNumber = "4500001234" }, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<BdcResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        Assert.Contains("still exists", body.Error!.Message);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DeleteTr_returns_422_when_a_different_SAP_error_is_reported_and_the_TR_still_exists()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BdcMessage("E", "L2", "020", "Some other error"))
            .ReturnsAsync(ExistsCheckResponse(trStillExists: true));

        var result = await _controller.DeleteTr(new DeleteTrRequest { TrNumber = "4500001234" }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── TR cleanup candidates ────────────────────────────────────────────────

    [Fact]
    public async Task GetTrCleanupCandidates_skips_the_LQUA_call_when_no_batches_are_found()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no data_display -> zero base rows -> zero batches

        var result = await _controller.GetTrCleanupCandidates(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the base query
    }

    [Fact]
    public async Task GetTrCleanupCandidates_queries_LQUA_once_for_the_distinct_batches_found_and_flags_reasons()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new()
                {
                    ["data_display"] = new()
                    {
                        new() { ["WA"] = "header" },
                        new() { ["WA"] = "101|4500001111|30005R|1710|10|EA|0000111111|5" }, // SLoc 1710 -> flagged
                    },
                },
            })
            .ReturnsAsync(new RfcResponse()); // LQUA call — no rows, batch not already transferred

        var result = await _controller.GetTrCleanupCandidates(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<TrCleanupCandidateRow[]>>(ok.Value);
        Assert.Single(body.Data!);
        Assert.Contains(WarehouseHelpers.ReasonSloc1710, body.Data![0].Reasons);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static ConsignmentMb1bRequest SampleConsignmentBody(bool testRun = false) => new()
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
        TestRun = testRun,
    };

    // MB1B is now BAPI_GOODSMVT_CREATE (pinned worker, ExecuteOnWorkerAsync);
    // the two LT01 legs are now L_TO_CREATE_SINGLE (ordinary ExecuteAsync).
    private static RfcResponse GoodsMvtResponse(string? materialDocument, string type = "S", string message = "Document posted") => new()
    {
        Parameters = materialDocument is null ? new() : new() { ["MATERIALDOCUMENT"] = materialDocument },
        Tables = new() { ["RETURN"] = new() { new() { ["TYPE"] = type, ["MESSAGE"] = message } } },
    };

    private static RfcResponse ToLegResponse(string? tanum, string type = "S", string message = "Transfer order created") => new()
    {
        Parameters = tanum is null ? new() : new() { ["E_TANUM"] = tanum },
        Tables = new() { ["RETURN"] = new() { new() { ["TYPE"] = type, ["MESSAGE"] = message } } },
    };

    [Fact]
    public async Task ConsignmentMb1b_returns_200_when_all_three_legs_succeed()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoodsMvtResponse("4973814993"));
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToLegResponse("0000504057"))
            .ReturnsAsync(ToLegResponse("0000504058"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), dryRun: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<ConsignmentMb1bResponse>>(ok.Value);
        Assert.True(body.Data!.Success);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Regression test for the Staging Post bug: SapServer previously always
    // returned 200/success:true for consignment-mb1b regardless of whether
    // MB1B actually posted in SAP, so a rejected goods movement (deficit
    // stock, missing authorization, etc.) looked identical to a real one to
    // every caller (routes/staging.js's Mark Delivered flow chief among
    // them) — the consignment stock never actually left SAP even though the
    // portal recorded the delivery as successful. Still holds after the
    // BAPI_GOODSMVT_CREATE switch.
    [Fact]
    public async Task ConsignmentMb1b_returns_422_when_the_MB1B_leg_is_rejected_by_SAP()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoodsMvtResponse(null, "E", "Deficit of SL stock 5 PC : 30005R 1000 SA B02"));
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToLegResponse("0000504057"))
            .ReturnsAsync(ToLegResponse("0000504058"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), dryRun: false, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<ConsignmentMb1bResponse>>(unprocessable.Value);
        Assert.False(body.Success);
        Assert.False(body.Data!.Success);
        Assert.Contains("Deficit of SL stock", body.Error!.Message);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsignmentMb1b_returns_422_when_either_LT01_leg_is_rejected_by_SAP()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoodsMvtResponse("4973814993"));
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToLegResponse("0000504057"))
            .ReturnsAsync(ToLegResponse(null, "E", "Bin does not exist"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), dryRun: false, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task ConsignmentMb1b_dryRun_returns_the_built_MB1B_request_without_acquiring_a_worker()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(), dryRun: true, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<RfcRequest>>(ok.Value);
        Assert.Equal(StockAdjustmentHelper.FnGoodsMvtCreate, body.Data!.FunctionName);
        _pool.Verify(p => p.AcquireWorker(), Times.Never);
    }

    [Fact]
    public async Task ConsignmentMb1b_TestRun_rolls_back_and_never_touches_the_LT01_legs()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, WarehouseHelpers.FnConsignment, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoodsMvtResponse("4973814993"));

        var result = await _controller.ConsignmentMb1b(SampleConsignmentBody(testRun: true), dryRun: false, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Never);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
