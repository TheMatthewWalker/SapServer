using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class CustomsControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly CustomsController _controller;

    public CustomsControllerTests()
    {
        _controller = new CustomsController(_pool.Object, _permissions.Object, NullLogger<CostingController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
    }

    [Fact]
    public async Task Lips_short_circuits_on_an_empty_deliveries_list()
    {
        var result = await _controller.Lips(new LipsRequest { Deliveries = [] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsType<ApiResponse<LipsRow[]>>(ok.Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Lips_queries_the_pool_for_a_non_empty_list()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.Lips(new LipsRequest { Deliveries = ["0080001234"] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Likp_short_circuits_on_an_empty_deliveries_list()
    {
        var result = await _controller.Likp(new LikpRequest { Deliveries = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<LikpRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Vbfa_short_circuits_on_an_empty_lines_list()
    {
        var result = await _controller.Vbfa(new VbfaRequest { Lines = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<VbfaRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Marc_short_circuits_on_an_empty_materials_list()
    {
        var result = await _controller.Marc(new MarcRequest { Materials = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<MarcRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Kna1_short_circuits_on_an_empty_customers_list()
    {
        var result = await _controller.Kna1(new Kna1Request { Customers = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<Kna1Row[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Kna1_still_returns_customer_rows_when_the_best_effort_KNVV_incoterms_lookup_throws()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()) // KNA1 lookup — parses to empty array here, but the call itself succeeds
            .ThrowsAsync(new InvalidOperationException("SAP session lost")); // KNVV lookup fails

        var result = await _controller.Kna1(new Kna1Request { Customers = ["0000100123"] }, CancellationToken.None);

        // The important assertion is that the KNVV failure doesn't propagate/500 —
        // the endpoint still returns 200 with whatever KNA1 already produced.
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Vbrk_short_circuits_on_an_empty_invoices_list()
    {
        var result = await _controller.Vbrk(new VbrkRequest { Invoices = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<VbrkRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Vbrk_queries_the_pool_for_a_non_empty_list()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.Vbrk(new VbrkRequest { Invoices = ["0004500099999"] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsignmentPrice_short_circuits_on_an_empty_lines_list()
    {
        var result = await _controller.ConsignmentPrice(new ConsignmentPriceRequest { Lines = [] }, CancellationToken.None);
        Assert.Empty(Assert.IsType<ApiResponse<ConsignmentPriceRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsignmentPrice_queries_the_pool_for_a_non_empty_list()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RfcResponse());
        var result = await _controller.ConsignmentPrice(new ConsignmentPriceRequest { Lines = [new ConsignmentPriceLine("363533", "CP1166")] }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
