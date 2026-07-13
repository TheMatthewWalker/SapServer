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

    // ── POST /api/warehouse/picksheet-stage-batch ─────────────────────────────
    //
    // Called whenever the operator adds a batch to a picksheet/pallet in the
    // warehouse portal. Ported from the wm_lt01.xltm macro's staging flow
    // (see PicksheetHelpers' "Staging" region for the full source mapping):
    //   1. Re-query LQUA fresh for the exact material+batch (don't trust
    //      whatever the frontend cached — stock can move in the meantime).
    //   2. Zero-pad the delivery/picksheet number to 10 digits → destination
    //      bin. Check LAGP for that bin; if missing, create it via a BDC on
    //      LS01 (storage type hardcoded "916", matching the macro), then
    //      re-check to confirm it actually exists before proceeding — "check
    //      before every picksheet transfer order to avoid failure".
    //   3. Create the transfer order (L_TO_CREATE_SINGLE) moving the batch's
    //      full on-hand quantity from its current bin into the picksheet bin.
    // No CheckPermissionAsync gate, same as the other picksheet-* endpoints —
    // called from Node via the shared service token.

    [HttpPost("picksheet-stage-batch")]
    [ProducesResponseType(typeof(ApiResponse<StagePicksheetBatchResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> PicksheetStageBatch(
        [FromBody] StagePicksheetBatchRequest request,
        CancellationToken ct)
    {
        StagePicksheetBatchResponse Failed(string error, List<SapReturnMessage>? messages = null) =>
            new(false, "", 0m, "", "", false, error, messages ?? []);

        // 1. Fresh batch snapshot
        var snapshotResponse = await _pool.ExecuteAsync(
            PicksheetHelpers.BuildBatchSnapshotRequest(request.Material, request.Batch), ct);
        var snapshot = PicksheetHelpers.ParseBatchSnapshot(snapshotResponse);

        if (snapshot is null)
        {
            var msg = $"Batch {request.Batch} of material {request.Material} was not found in stock (LQUA). It may have already been moved or consumed.";
            return UnprocessableEntity(ApiResponse<StagePicksheetBatchResponse>.Fail("422", msg, Failed(msg)));
        }

        // 2. Destination bin = picksheet number, zero-padded to 10 digits
        var destinationBin = SapPad.Pad(request.DeliveryNumber, 10);

        var binCheckResponse = await _pool.ExecuteAsync(PicksheetHelpers.BuildBinCheckRequest(destinationBin), ct);
        var binWasCreated = false;

        if (!PicksheetHelpers.BinExists(binCheckResponse))
        {
            var createResponse = await _pool.ExecuteAsync(PicksheetHelpers.BuildCreateBinRequest(destinationBin), ct);
            var createMessage   = ReturnTableHelper.GetParam(createResponse, "MESSG") ?? "";

            // Re-check rather than trust the BDC's own message text (the macro
            // treats one specific message string as "actually succeeded", which
            // is too brittle to port as-is) — confirm the bin is really there.
            var recheckResponse = await _pool.ExecuteAsync(PicksheetHelpers.BuildBinCheckRequest(destinationBin), ct);
            if (!PicksheetHelpers.BinExists(recheckResponse))
            {
                var msg = $"Could not create staging bin {destinationBin} (storage type {PicksheetHelpers.StagingStorageType}) in SAP. LS01 response: {createMessage}";
                return UnprocessableEntity(ApiResponse<StagePicksheetBatchResponse>.Fail("422", msg, Failed(msg)));
            }

            binWasCreated = true;
        }

        // 3. Create the transfer order — full on-hand quantity of the batch,
        // from its current bin into the picksheet's staging bin.
        var transferOrderBody = new CreateTransferOrderRequest
        {
            StorageLocation = snapshot.StorageLocation,
            Material        = snapshot.Material,
            Quantity        = snapshot.TotalQty,
            SourceType      = snapshot.StorageType,
            SourceBin       = snapshot.Bin,
            DestinationType = PicksheetHelpers.StagingStorageType,
            DestinationBin  = destinationBin,
            Batch           = snapshot.Batch
        };

        var toResponse = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(transferOrderBody), ct);
        var toResult    = WarehouseHelpers.ParseTransferOrderResponse(toResponse);

        if (ReturnTableHelper.HasBlockingError(toResult.Messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))))
        {
            const string msg = "SAP rejected the transfer order.";
            return UnprocessableEntity(ApiResponse<StagePicksheetBatchResponse>.Fail("422", msg, Failed(msg, toResult.Messages)));
        }

        return Ok(ApiResponse<StagePicksheetBatchResponse>.Ok(new StagePicksheetBatchResponse(
            Success:             true,
            TransferOrderNumber: toResult.TransferOrderNumber,
            QuantityMoved:       snapshot.TotalQty,
            DestinationBin:      destinationBin,
            DestinationType:     PicksheetHelpers.StagingStorageType,
            BinWasCreated:       binWasCreated,
            Error:               null,
            Messages:            toResult.Messages)));
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
