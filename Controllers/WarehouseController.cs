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

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "stock");

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

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "stock/totals");

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

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "stock/bins");

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

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "transfer-order");

        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(body), ct);
        return Ok(ApiResponse<CreateTransferOrderResponse>.Ok(
            WarehouseHelpers.ParseTransferOrderResponse(response)));
    }

    // ── POST /api/warehouse/picksheet-stock ───────────────────────────────────
    //
    // LQUA + ZPRODBATCH joined on batch, filtered to a specific material list —
    // backs the picksheet builder's "what stock is available" panel. No
    // CheckPermissionAsync gate, matching CustomsController's endpoints: this
    // is called from Node via the shared service token (userId 0), same as
    // /api/sap/lips, /api/sap/likp etc., not the per-user token that
    // CheckPermissionAsync expects.

    [HttpPost("picksheet-stock")]
    [ProducesResponseType(typeof(ApiResponse<PicksheetBatchRow[]>), 200)]
    public async Task<IActionResult> PicksheetStock([FromBody] PicksheetStockRequest request, CancellationToken ct)
    {
        if (request.Materials.Count == 0)
            return Ok(ApiResponse<PicksheetBatchRow[]>.Ok([]));

        var rfcRequest = PicksheetHelpers.BuildStockRequest(request);
        var response    = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<PicksheetBatchRow[]>.Ok(PicksheetHelpers.ParseStockRows(response)));
    }

    // ── POST /api/warehouse/picksheet-materials ────────────────────────────────
    //
    // LIPS filtered on LFIMG (delivery quantity, populated as soon as the
    // delivery exists) rather than KCMENG (confirmed quantity, only populated
    // once picked) — see PicksheetHelpers.LipsColumns for the full reasoning.
    // No CheckPermissionAsync gate, same as picksheet-stock above.

    [HttpPost("picksheet-materials")]
    [ProducesResponseType(typeof(ApiResponse<PicksheetLipsRow[]>), 200)]
    public async Task<IActionResult> PicksheetMaterials([FromBody] PicksheetLipsRequest request, CancellationToken ct)
    {
        if (request.Deliveries.Count == 0)
            return Ok(ApiResponse<PicksheetLipsRow[]>.Ok([]));

        var rfcRequest = PicksheetHelpers.BuildLipsRequest(request);
        var response    = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<PicksheetLipsRow[]>.Ok(PicksheetHelpers.ParseLipsRows(response)));
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

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "consignment-mb1b");

        var mb1b   = await _pool.ExecuteAsync(WarehouseHelpers.BuildMb1bRequest(body),          ct);
        var toNonC = await _pool.ExecuteAsync(WarehouseHelpers.BuildToNonConsignRequest(body),   ct);
        var toC    = await _pool.ExecuteAsync(WarehouseHelpers.BuildToConsignRequest(body),      ct);
        return Ok(ApiResponse<ConsignmentMb1bResponse>.Ok(
            WarehouseHelpers.ParseConsignmentResponse(mb1b, toNonC, toC)));
    }



}
