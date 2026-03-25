using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/warehouse")]
public sealed class WarehouseController : SapControllerBase
{
    public WarehouseController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<WarehouseController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/warehouse/stock ──────────────────────────────────────────────

    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<StockRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStock([FromQuery] StockQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);
        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildStockRequest(query), ct);
        return Ok(ApiResponse<StockRow[]>.Ok(WarehouseHelpers.ParseStockRows(response)));
    }

    // ── GET /api/warehouse/stock/totals ───────────────────────────────────────

    [HttpGet("stock/totals")]
    [ProducesResponseType(typeof(ApiResponse<MaterialTotalRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStockTotals([FromQuery] StockQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);
        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildStockRequest(query), ct);
        return Ok(ApiResponse<MaterialTotalRow[]>.Ok(
            WarehouseHelpers.AggregateByMaterial(WarehouseHelpers.ParseStockRows(response))));
    }

    // ── GET /api/warehouse/stock/bins ─────────────────────────────────────────

    [HttpGet("stock/bins")]
    [ProducesResponseType(typeof(ApiResponse<BinSummaryRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStockBins([FromQuery] StockQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);
        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildStockRequest(query), ct);
        return Ok(ApiResponse<BinSummaryRow[]>.Ok(
            WarehouseHelpers.AggregateByBin(WarehouseHelpers.ParseStockRows(response))));
    }

    // ── POST /api/warehouse/transfer-order ────────────────────────────────────

    [HttpPost("transfer-order")]
    [ProducesResponseType(typeof(ApiResponse<CreateTransferOrderResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> CreateTransferOrder(
        [FromBody] CreateTransferOrderRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnCreateTo, ct);
        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(body), ct);
        return Ok(ApiResponse<CreateTransferOrderResponse>.Ok(
            WarehouseHelpers.ParseTransferOrderResponse(response)));
    }

    // ── POST /api/warehouse/consignment-mb1b ──────────────────────────────────

    [HttpPost("consignment-mb1b")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentMb1bResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ConsignmentMb1b(
        [FromBody] ConsignmentMb1bRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnConsignment, ct);
        var mb1b   = await _pool.ExecuteAsync(WarehouseHelpers.BuildMb1bRequest(body),          ct);
        var toNonC = await _pool.ExecuteAsync(WarehouseHelpers.BuildToNonConsignRequest(body),   ct);
        var toC    = await _pool.ExecuteAsync(WarehouseHelpers.BuildToConsignRequest(body),      ct);
        return Ok(ApiResponse<ConsignmentMb1bResponse>.Ok(
            WarehouseHelpers.ParseConsignmentResponse(mb1b, toNonC, toC)));
    }
}
