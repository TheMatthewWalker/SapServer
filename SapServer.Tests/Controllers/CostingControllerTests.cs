using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;
using System.Web.Http;

namespace SapServer.Tests.Controllers;

public class CostingControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly CostingController _controller;

    public CostingControllerTests()
    {
        _controller = new CostingController(_pool.Object, _permissions.Object, NullLogger<CostingController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task GetCostSheet_dryRun_never_calls_the_pool()
    {
        var result = await _controller.GetCostSheet(new CostSheetRequest { Date = "01.01.2026" }, dryRun: true, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPeriodBalance_queries_once_per_GL_account_and_flattens_the_results()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetPeriodBalance(
            new PeriodBalanceRequest { FiscalYear = "2026", PeriodFrom = "1", PeriodTo = "3", GlAccounts = ["100000", "200000", "300000"] },
            CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task GetProfitCenter_returns_the_parsed_rows()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.GetProfitCenter(new ProfitCenterRequest { DateFrom = "01.01.2026", DateTo = "31.01.2026" }, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostFreight_commits_when_SAP_returns_an_accounting_number()
    {
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["OBJ_KEY"] = "14000001232026" } });

        var result = await _controller.PostFreight(new FreightPostingRequest { DocDate = "20260101", Vendor = "0000100123", Amount = 500, Currency = "GBP" }, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostFreight_rolls_back_and_returns_BadRequest_when_no_accounting_number_comes_back()
    {
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.PostFreight(new FreightPostingRequest { DocDate = "20260101", Vendor = "0000100123", Amount = 500, Currency = "GBP" }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostFreight_returns_BadRequest_without_calling_the_pool_when_DocDate_is_not_yyyyMMdd()
    {
        // Regression test: BuildFreightPostingRequest passes DocDate straight
        // through to NCo's DATE setter with no parsing — a dd.MM.yyyy value
        // (accepted by other date fields in this app, e.g. CostSheetRequest)
        // crashed with an unhandled RfcTypeConversionException instead of
        // failing cleanly (endpoint-test-log-2026-08-27.md, ROUND 2 #22/23).
        var result = await _controller.PostFreight(new FreightPostingRequest { DocDate = "01.01.2026", Vendor = "0000100123", Amount = 500, Currency = "GBP" }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.AcquireWorkerAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostFreightBatch_posts_each_request_and_collects_one_row_per_request()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["OBJ_KEY"] = "14000009992026" } });

        var requests = Enumerable.Range(1, 5)
            .Select(i => new FreightPostingRequest { DocDate = "20260101", Vendor = $"000010{i:0000}", Amount = 100 * i, Currency = "GBP" })
            .ToList();

        var result = await _controller.PostFreightBatch(requests, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<FreightPostingRow>>>(ok);
        Assert.Equal(5, body.Data!.Count);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [Fact]
    public async Task PostFreightBatch_fails_just_the_one_item_with_a_bad_DocDate_without_calling_the_pool_for_it()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["OBJ_KEY"] = "14000009992026" } });

        var requests = new List<FreightPostingRequest>
        {
            new() { DocDate = "20260101", Vendor = "0000100001", Amount = 100, Currency = "GBP" },
            new() { DocDate = "01.01.2026", Vendor = "0000100002", Amount = 200, Currency = "GBP" },
        };

        var result = await _controller.PostFreightBatch(requests, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<FreightPostingRow>>>(ok);
        Assert.Equal(2, body.Data!.Count);
        Assert.Contains(body.Data, r => !r.Success && r.Messages.Any(m => m.Message.Contains("yyyyMMdd")));
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the valid item
    }
}
