using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapServer.Configuration;
using SapServer.Controllers;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;
using System.Web.Http;

namespace SapServer.Tests.Controllers;

public class PurchasingControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly PurchasingController _controller;

    public PurchasingControllerTests()
    {
        var poolOptions = Options.Create(new SapNcoOptions
        {
            ServiceAccount = new SapConnectionOptions { AppServerHost = "sap-test-host", Client = "100", SystemNumber = "01", Language = "EN" },
        });
        _controller = new PurchasingController(_pool.Object, _permissions.Object, poolOptions, NullLogger<PurchasingController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static PoCreateRequest SamplePo() => new()
    {
        Vendor = "0000100123",
        Currency = "GBP",
        Items = [new PoCreateItem { Material = "30005R", ShortText = "Test", Quantity = 10, Unit = "KG", NetPrice = 5 }],
    };

    [Fact]
    public async Task CreatePurchaseOrder_dryRun_never_acquires_a_worker()
    {
        var result = await _controller.CreatePurchaseOrder(SamplePo(), dryRun: true, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        Assert.IsType<ApiResponse<RfcRequest>>(ok);
        _pool.Verify(p => p.AcquireWorkerAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePurchaseOrder_commits_on_success()
    {
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["EXPPURCHASEORDER"] = "4500001234" } });

        var result = await _controller.CreatePurchaseOrder(SamplePo(), dryRun: false, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<PoCreateRow>>(ok);
        Assert.Equal("4500001234", body.Data!.PurchaseOrder);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrder_rolls_back_and_returns_BadRequest_when_SAP_reports_no_PO_number()
    {
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var result = await _controller.CreatePurchaseOrder(SamplePo(), dryRun: false, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result); // note: BadRequest here, not UnprocessableEntity like WarehouseController
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostGoodsReceipt_dryRun_never_calls_the_pool()
    {
        var result = await _controller.PostGoodsReceipt(new GoodsReceiptRequest { PurchaseOrder = "4500001234", LineNumber = 1 }, dryRun: true, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostGoodsReceipt_parses_the_BDC_response_on_success()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MESSG"] = "S    M7   001 Goods receipt document 5000009999 posted" } });

        var result = await _controller.PostGoodsReceipt(new GoodsReceiptRequest { PurchaseOrder = "4500001234", LineNumber = 1 }, dryRun: false, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<BdcResponse>>(ok);
        Assert.Equal("5000009999", body.Data!.DocumentNumber);
    }

    [Fact]
    public async Task GetPoPrice_returns_the_parsed_price_map()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse
            {
                Tables = new() { ["POCOND"] = [new() { ["ITM_NUMBER"] = "00010", ["COND_TYPE"] = "PB00", ["COND_VALUE"] = "89,25" }] },
            });

        var result = await _controller.GetPoPrice("4500001234", CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<Dictionary<string, decimal>>>(ok);
        Assert.Equal(89.25m, body.Data!["00010"]);
    }

    [Fact]
    public async Task CreatePurchaseOrderAndReceipt_requires_SAP_credentials_before_touching_the_pool()
    {
        var result = await _controller.CreatePurchaseOrderAndReceipt(
            new CreatePoAndReceiptRequest { Items = [new CreatePoAndReceiptItem { Material = "30005R", Quantity = 1 }] }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePurchaseOrderAndReceipt_requires_at_least_one_item()
    {
        var result = await _controller.CreatePurchaseOrderAndReceipt(
            new CreatePoAndReceiptRequest { SapUsername = "j.smith", SapPassword = "pw", Items = [] }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
    }

    [Fact]
    public async Task CreatePurchaseOrderAndReceipt_always_releases_the_elevated_worker_even_when_PO_creation_fails()
    {
        _pool.Setup(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SapWorkerHandle)null!);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no EXPPURCHASEORDER -> PO creation "fails"

        var result = await _controller.CreatePurchaseOrderAndReceipt(new CreatePoAndReceiptRequest
        {
            SapUsername = "j.smith",
            SapPassword = "pw",
            Items = [new CreatePoAndReceiptItem { Material = "30005R", Quantity = 1 }],
        }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
        _pool.Verify(p => p.ReleaseElevatedWorkerAsync(It.IsAny<SapWorkerHandle>()), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderAndReceipt_reports_each_goods_receipt_line_independently_including_a_failure()
    {
        _pool.Setup(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SapWorkerHandle)null!);

        _pool.SetupSequence(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["EXPPURCHASEORDER"] = "4500001234" } }) // PO create
            .ReturnsAsync(new RfcResponse()) // BAPI_TRANSACTION_COMMIT (return value unused)
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MESSG"] = "S    M7   001 Goods receipt document 5000000001 posted" } }) // GR line 1
            .ThrowsAsync(new InvalidOperationException("SAP session lost")); // GR line 2 fails

        var result = await _controller.CreatePurchaseOrderAndReceipt(new CreatePoAndReceiptRequest
        {
            SapUsername = "j.smith",
            SapPassword = "pw",
            Items =
            [
                new CreatePoAndReceiptItem { Material = "30005R", Quantity = 1 },
                new CreatePoAndReceiptItem { Material = "30006R", Quantity = 2 },
            ],
        }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<CreatePoAndReceiptResponse>>(ok);
        Assert.True(body.Data!.PoSuccess);
        Assert.Equal(2, body.Data.Lines.Count);
        Assert.True(body.Data.Lines[0].Success);
        Assert.Equal("5000000001", body.Data.Lines[0].DocumentNumber);
        Assert.False(body.Data.Lines[1].Success);
        Assert.Contains("SAP session lost", body.Data.Lines[1].Error);

        _pool.Verify(p => p.ReleaseElevatedWorkerAsync(It.IsAny<SapWorkerHandle>()), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderElevated_requires_credentials()
    {
        var result = await _controller.CreatePurchaseOrderElevated(
            new CreatePoElevatedRequest { Items = [new PoCreateItem { Material = "30005R", Quantity = 1 }] }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
        _pool.Verify(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePurchaseOrderElevated_commits_and_releases_on_success()
    {
        _pool.Setup(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SapWorkerHandle)null!);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["EXPPURCHASEORDER"] = "4500009999" } });

        var result = await _controller.CreatePurchaseOrderElevated(new CreatePoElevatedRequest
        {
            SapUsername = "j.smith",
            SapPassword = "pw",
            Items = [new PoCreateItem { Material = "30005R", Quantity = 1 }],
        }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<CreatePoElevatedResponse>>(ok);
        Assert.Equal("4500009999", body.Data!.PurchaseOrder);
        _pool.Verify(p => p.ReleaseElevatedWorkerAsync(It.IsAny<SapWorkerHandle>()), Times.Once);
    }
}
