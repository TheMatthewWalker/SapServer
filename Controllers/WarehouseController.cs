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

        // PRCTR lives on MARC, not LQUA, so Profit Centre can only be filled in
        // via a second, unfiltered whole-plant MATNR→PRCTR pull (same pattern as
        // PerformanceController's stock endpoint). That pull is expensive, so it
        // only runs when a caller actually filters by Profit Centre — an
        // ordinary stock search skips it and gets rows back with ProfitCentre
        // left blank.
        StockRow[] rows;
        if (!string.IsNullOrWhiteSpace(query.ProfitCentre))
        {
            var pcResponse = await _pool.ExecuteAsync(PerformanceHelpers.BuildMaterialProfitCentre(), ct);
            var profitCentres = PerformanceHelpers.ParseMaterialProfitCentre(pcResponse);

            rows = WarehouseHelpers.ParseStockRows(response, profitCentres)
                .Where(r => string.Equals(r.ProfitCentre, query.ProfitCentre, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            rows = WarehouseHelpers.ParseStockRows(response);
        }

        return Ok(ApiResponse<StockRow[]>.Ok(rows));
    }

    // ── GET /api/warehouse/im-stock ────────────────────────────────────────────
    //
    // MARD-based unrestricted stock (LABST) for a plant/storage-location —
    // storage location 1716 (Production Count) has no WM/bin concept and
    // never appears in LQUA, confirmed against the real SAP system. Same
    // FnReadTables permission gate as GetStock, since it's the same
    // underlying read-only RFC.
    [HttpGet("im-stock")]
    [ProducesResponseType(typeof(ApiResponse<ImStockRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetImStock([FromQuery] ImStockQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);

        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildImStockRequest(query), ct);
        return Ok(ApiResponse<ImStockRow[]>.Ok(WarehouseHelpers.ParseImStockRows(response)));
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
    //
    // Checks the destination bin actually exists (LAGP) before ever calling
    // L_TO_CREATE_SINGLE. That RFC doesn't fail cleanly for a non-existent
    // bin — no SAP.Exception code, nothing in RETURN, just the call failing
    // outright and the OCX connection needing to reconnect — so a typo'd bin
    // used to surface as "RFC call ... failed (no detail available)" with no
    // indication of what was actually wrong, and cost the user a second
    // attempt while the session reconnected. Failing fast here means a bad
    // bin gets a clear, immediate 422 instead, and SAP is never called at all.
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

        // Pad here before the LAGP existence check, not just inside
        // BuildTransferOrderRequest below — LAGP~LGPLA stores bin codes
        // zero-padded to 10 characters, so a user typing e.g. "123" instead
        // of "0000000123" was failing this pre-check (a false "bin does not
        // exist") even though the padded RFC call further down would have
        // worked fine. SapPad.Pad is idempotent on already-padded/non-numeric
        // values, so this is safe to apply unconditionally. See the picksheet
        // staging flow (PicksheetStageBatch) for the same pad-then-check order.
        var destinationBin = SapPad.Pad(body.DestinationBin, 10);

        var binCheck = await _pool.ExecuteAsync(
            WarehouseHelpers.BuildBinCheckRequest(body.DestinationType, destinationBin), ct);

        if (!WarehouseHelpers.BinExists(binCheck))
        {
            var msg = $"Destination bin {body.DestinationType}/{destinationBin} does not exist in SAP warehouse {WarehouseHelpers.Warehouse}. Check the storage type and bin and try again.";
            return UnprocessableEntity(ApiResponse<CreateTransferOrderResponse>.Fail("422", msg,
                new CreateTransferOrderResponse { Success = false, Messages = [new SapReturnMessage { Type = "E", Message = msg }] }));
        }

        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(body), ct);
        return Ok(ApiResponse<CreateTransferOrderResponse>.Ok(
            WarehouseHelpers.ParseTransferOrderResponse(response)));
    }

    // ── POST /api/warehouse/stock-adjustment ──────────────────────────────────
    //
    // Movement types 711/712 (unrestricted stock) or 717/718 (category 'S',
    // blocked stock — 711/712 aren't valid against it) via
    // BAPI_GOODSMVT_CREATE — see StockAdjustmentModels.cs for the full
    // history/caveat: GoodsReceiptHelper already found this same BAPI
    // doesn't work against this SAP system for a different movement (101,
    // GR-for-PO via GM_CODE "01"), and had to fall back to a BDC recording
    // of MB01 instead. This uses a different GM_CODE branch ("06", goods
    // movements without reference — the code path 711/712/717/718 normally
    // go through via transaction MB1C), so it's untested against this
    // system rather than known-broken — test it via test.http before wiring
    // this into Node, exactly as the user asked. Confirmed working (711)
    // against real data by the user since.
    //
    // Unlike the BDC calls elsewhere in this file, BAPI_GOODSMVT_CREATE is a
    // real BAPI: like BAPI_PO_CREATE1 (PurchasingController.CreatePurchaseOrder),
    // it needs an explicit BAPI_TRANSACTION_COMMIT/ROLLBACK on the same pinned
    // worker, or nothing actually persists in SAP.
    [HttpPost("stock-adjustment")]
    [ProducesResponseType(typeof(ApiResponse<StockAdjustmentResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> CreateStockAdjustment(
        [FromBody] StockAdjustmentRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), StockAdjustmentHelper.FnGoodsMvtCreate, ct);

        if (!StockAdjustmentHelper.ExpectedMovementTypes.Contains(body.MovementType))
            return UnprocessableEntity(ApiResponse<StockAdjustmentResponse>.Fail("422",
                $"Movement type must be 711, 712, 717, or 718 for a stock adjustment (got '{body.MovementType}').",
                new StockAdjustmentResponse { Success = false }));

        var request = StockAdjustmentHelper.BuildStockAdjustmentRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var worker = _pool.AcquireWorker();

        var data     = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
        var response = StockAdjustmentHelper.ParseStockAdjustmentResponse(data);

        if (body.TestRun)
        {
            // A test run never creates a real document, so there's nothing
            // to commit — roll back to release whatever SAP locked while
            // simulating the posting.
            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return Ok(ApiResponse<StockAdjustmentResponse>.Ok(response));
        }

        if (!response.Success)
        {
            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return UnprocessableEntity(ApiResponse<StockAdjustmentResponse>.Fail(
                "422", "SAP rejected the stock adjustment. Transaction rolled back.", response));
        }

        await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
        return Ok(ApiResponse<StockAdjustmentResponse>.Ok(response));
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
            new(false, "", 0m, "", "", false, "", "", error, messages ?? []);

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
            SourceType:          snapshot.StorageType,
            SourceBin:           snapshot.Bin,
            Error:               null,
            Messages:            toResult.Messages)));
    }

    // ── POST /api/warehouse/picksheet-unstage-batch ───────────────────────────
    //
    // Reverses a picksheet-stage-batch transfer order — called when a staged
    // package is deleted from a pallet, so the batch's stock is freed up for
    // other deliveries again. See PicksheetHelpers.ShouldReverse for the
    // "nothing to reverse" handling (batch already picked/moved elsewhere).
    // No CheckPermissionAsync gate, same as the other picksheet-* endpoints.

    [HttpPost("picksheet-unstage-batch")]
    [ProducesResponseType(typeof(ApiResponse<PicksheetUnstageBatchResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> PicksheetUnstageBatch(
        [FromBody] PicksheetUnstageBatchRequest request,
        CancellationToken ct)
    {
        PicksheetUnstageBatchResponse Failed(string error, List<SapReturnMessage>? messages = null) =>
            new(false, "", 0m, false, error, messages ?? []);

        var snapshotResponse = await _pool.ExecuteAsync(
            PicksheetHelpers.BuildBatchSnapshotRequest(request.Material, request.Batch), ct);
        var snapshot = PicksheetHelpers.ParseBatchSnapshot(snapshotResponse);

        if (!PicksheetHelpers.ShouldReverse(snapshot, request.StagedBin))
        {
            // Not sitting in the picksheet bin anymore (already picked, or
            // moved by something else since) — nothing to undo, but that's
            // not a failure; the caller (palletpackages delete) can proceed.
            return Ok(ApiResponse<PicksheetUnstageBatchResponse>.Ok(
                new PicksheetUnstageBatchResponse(true, "", 0m, true, null, [])));
        }

        var body     = PicksheetHelpers.BuildUnstageTransferOrderBody(snapshot!, request.OriginalSourceType, request.OriginalSourceBin);
        var toResponse = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(body), ct);
        var toResult    = WarehouseHelpers.ParseTransferOrderResponse(toResponse);

        if (ReturnTableHelper.HasBlockingError(toResult.Messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))))
        {
            const string msg = "SAP rejected the reversing transfer order.";
            return UnprocessableEntity(ApiResponse<PicksheetUnstageBatchResponse>.Fail("422", msg, Failed(msg, toResult.Messages)));
        }

        return Ok(ApiResponse<PicksheetUnstageBatchResponse>.Ok(new PicksheetUnstageBatchResponse(
            Success:             true,
            TransferOrderNumber: toResult.TransferOrderNumber,
            QuantityMoved:       snapshot!.TotalQty,
            NothingToReverse:    false,
            Error:               null,
            Messages:            toResult.Messages)));
    }

    // ── GET /api/warehouse/open-transfer-requirements ─────────────────────────
    //
    // Backs the Transfer Requirements (LT04) tile — lists open TRs (LTBK/LTBP
    // joined to MARC for MRP controller and MKPF for the doc text), ready to
    // be turned into a confirmed TO. Same ZRFC_READ_TABLES permission gate as
    // GetStock above, since it's the same read-only RFC. Optional
    // mrpController filter lets the frontend narrow the list the same way
    // the source Excel macro's operators already work by MRP controller.

    [HttpGet("open-transfer-requirements")]
    [ProducesResponseType(typeof(ApiResponse<OpenTransferRequirementRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetOpenTransferRequirements(
        [FromQuery] OpenTransferRequirementsQuery query,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);

        var response = await _pool.ExecuteAsync(
            WarehouseHelpers.BuildOpenTransferRequirementsRequest(query), ct);
        return Ok(ApiResponse<OpenTransferRequirementRow[]>.Ok(
            WarehouseHelpers.ParseOpenTransferRequirementRows(response)));
    }

    // ── GET /api/warehouse/bin-storage-types ──────────────────────────────────
    //
    // Given a storage bin, returns every storage type LAGP has it registered
    // under (usually exactly one) — backs the shared "auto-derive storage
    // type from a scanned/typed bin" QoL feature used across the LT04 scan
    // flow, the LT04 modal, both Stock Management transfer forms, and the
    // Transfer Orders tile. Read-only (same FnReadTables gate as GetStock),
    // so no LOG_SUPER-style restriction — just a lookup.
    [HttpGet("bin-storage-types")]
    [ProducesResponseType(typeof(ApiResponse<string[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetBinStorageTypes([FromQuery] string bin, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);

        if (string.IsNullOrWhiteSpace(bin))
            return Ok(ApiResponse<string[]>.Ok([]));

        // Padded here, before the LAGP lookup — same convention
        // CreateTransferOrder already uses for its own BuildBinCheckRequest
        // call: LAGP~LGPLA stores bin codes zero-padded to 10 characters, so
        // an unpadded "123" would silently report zero matches for a bin
        // that actually exists as "0000000123".
        var paddedBin = SapPad.Pad(bin, 10);
        var response = await _pool.ExecuteAsync(
            WarehouseHelpers.BuildBinStorageTypeLookupRequest(paddedBin), ct);
        return Ok(ApiResponse<string[]>.Ok(WarehouseHelpers.ParseBinStorageTypeRows(response)));
    }

    // ── POST /api/warehouse/create-lt04 ───────────────────────────────────────
    //
    // Replicates transaction LT04 (create + auto-confirm a TO from an open TR)
    // exactly as recorded in wm_lt01.xltm's create_LT04 — see
    // WarehouseHelpers.BuildCreateLt04Request for the full screen mapping.
    // Runs create_LT04's own quality pre-check first (LQUA~BESTQ = 'Q' means
    // the batch hasn't been scanned out of firewall yet) and fails fast with
    // a 422 before ever calling LT04, mirroring the bin-existence check in
    // CreateTransferOrder above. Gated on FnConsignment (Z_RFC_CALL_TRANSACTION)
    // rather than a bespoke permission — it's the same underlying RFC every
    // other BDC-driven transaction in this controller (consignment-mb1b) is
    // gated on.
    [HttpPost("create-lt04")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> CreateLt04(
        [FromBody] CreateLt04Request body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnConsignment, ct);

        var qualityCheck = await _pool.ExecuteAsync(
            WarehouseHelpers.BuildQualityBlockCheckRequest(body.Material, body.PalletOrBatch), ct);

        if (WarehouseHelpers.IsQualityBlocked(qualityCheck))
        {
            const string msg = "This has not been scanned out of firewall yet. Unable to LT04.";
            return UnprocessableEntity(ApiResponse<BdcResponse>.Fail("422", msg,
                new BdcResponse { Type = "E", Message = msg }));
        }

        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildCreateLt04Request(body), ct);
        var result   = ProductionHelpers.ParseBdcResponse(response);
        return Ok(ApiResponse<BdcResponse>.Ok(result));
    }

    // ── POST /api/warehouse/delete-tr ─────────────────────────────────────────
    //
    // Replicates wm_open_tr.xlsm's ati_code.delete_tr (transaction LB02) —
    // see WarehouseHelpers.BuildDeleteTrRequest for the full screen mapping.
    // Gated on FnConsignment, same as CreateLt04/ConsignmentMb1b — the same
    // underlying RFC every BDC-driven transaction in this controller uses.
    // SapServer itself doesn't gate this any more tightly than that; the
    // portal-role restriction (LOG_SUPER, since deleting a TR is unrecoverable)
    // is enforced one layer up in Normanton-Nexus's routes/sap.js proxy.
    //
    // Two things a naive "just trust the BDC message" implementation gets
    // wrong here, both fixed below:
    //  1. SAP sometimes refuses with "E L2 019 You are not allowed to delete
    //     transfer requirement item 0001" — surfaced directly as a 422
    //     rather than retried with an unconfirmed fallback screen mapping
    //     (see WarehouseHelpers.BuildDeleteTrRequest's comment for why).
    //  2. Even a non-blocked response can't be trusted at face value — this
    //     flow was observed reporting Type "S" for a framework-level BDC
    //     failure (a field that doesn't exist on the target dynpro) that
    //     deleted nothing. Every non-blocked attempt is verified by
    //     re-querying LTBP before reporting success.
    [HttpPost("delete-tr")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> DeleteTr([FromBody] DeleteTrRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnConsignment, ct);

        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildDeleteTrRequest(body.TrNumber), ct);
        var result   = ProductionHelpers.ParseBdcResponse(response);

        if (WarehouseHelpers.IsDeleteTrItemBlocked(result))
        {
            var msg = $"SAP refused to delete TR {body.TrNumber}: {result.Message}. This TR needs a manual LB02 delete.";
            return UnprocessableEntity(ApiResponse<BdcResponse>.Fail("422", msg,
                new BdcResponse { Type = "E", Message = msg, RawMessage = result.RawMessage }));
        }

        var existsCheck = await _pool.ExecuteAsync(WarehouseHelpers.BuildTrExistsRequest(body.TrNumber), ct);
        if (WarehouseHelpers.TrStillExists(existsCheck))
        {
            var msg = $"SAP reported \"{result.Message}\" but TR {body.TrNumber} still exists — the delete did not take effect.";
            return UnprocessableEntity(ApiResponse<BdcResponse>.Fail("422", msg,
                new BdcResponse { Type = "E", Message = msg, RawMessage = result.RawMessage }));
        }

        return Ok(ApiResponse<BdcResponse>.Ok(result));
    }

    // ── GET /api/warehouse/tr-cleanup-candidates ──────────────────────────────
    //
    // Automates the judgment call wm_open_tr.xlsm's operators have always
    // made by eyeballing the macro's raw data columns — see
    // WarehouseHelpers.BuildTrCleanupCandidateRows for the three reason
    // conditions. Read-only (FnReadTables), so no LOG_SUPER restriction here;
    // only the resulting bulk delete (via delete-tr) needs it.
    [HttpGet("tr-cleanup-candidates")]
    [ProducesResponseType(typeof(ApiResponse<TrCleanupCandidateRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetTrCleanupCandidates(CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnReadTables, ct);

        var baseResponse = await _pool.ExecuteAsync(WarehouseHelpers.BuildTrCleanupCandidatesBaseRequest(), ct);
        var baseRows     = WarehouseHelpers.ParseTrCleanupBaseRows(baseResponse);

        var batches = baseRows
            .Select(r => r.Batch)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct()
            .ToArray();

        RfcResponse? lquaResponse = batches.Length > 0
            ? await _pool.ExecuteAsync(WarehouseHelpers.BuildTrCleanupLquaByBatchRequest(batches), ct)
            : null;

        return Ok(ApiResponse<TrCleanupCandidateRow[]>.Ok(
            WarehouseHelpers.BuildTrCleanupCandidateRows(baseRows, lquaResponse)));
    }

    // ── POST /api/warehouse/consignment-mb1b ──────────────────────────────────

    [HttpPost("consignment-mb1b")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentMb1bResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> ConsignmentMb1b(
        [FromBody] ConsignmentMb1bRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), WarehouseHelpers.FnConsignment, ct);

        var mb1bRequest = WarehouseHelpers.BuildMb1bRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(mb1bRequest));

        // MB1B is now a real BAPI (BAPI_GOODSMVT_CREATE) rather than a BDC —
        // it needs a pinned worker so the commit/rollback below runs on the
        // same SAP session as the BAPI call itself, same pattern as
        // CreateStockAdjustment/PurchasingController.CreatePurchaseOrder.
        // The two LT01 legs stay on ordinary ExecuteAsync calls (via
        // L_TO_CREATE_SINGLE) — that RFC commits itself, same as the plain
        // transfer-order endpoint already relies on.
        var worker   = _pool.AcquireWorker();
        var mb1bData = await _pool.ExecuteOnWorkerAsync(worker, mb1bRequest, ct);

        if (body.TestRun)
        {
            // A test run never creates a real material document — nothing
            // to commit, and nothing sensible to build the two transfer
            // orders against, so stop here (mirrors CreateStockAdjustment's
            // TestRun handling).
            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return Ok(ApiResponse<ConsignmentMb1bResponse>.Ok(WarehouseHelpers.ParseMb1bOnly(mb1bData)));
        }

        await _pool.ExecuteOnWorkerAsync(worker, WarehouseHelpers.Mb1bSucceeded(mb1bData)
            ? CommitHelper.BuildBapiCommit()
            : CommitHelper.BuildBapiRollback(), ct);

        // Both LT01 legs still run unconditionally, same as before the BDC
        // replacement — a rejected MB1B (deficit stock, etc.) is reported
        // alongside whatever the transfer-order legs did rather than
        // short-circuited, so the combined message always reflects every
        // leg's real outcome.
        var toNonC = await _pool.ExecuteAsync(WarehouseHelpers.BuildToNonConsignRequest(body), ct);
        var toC    = await _pool.ExecuteAsync(WarehouseHelpers.BuildToConsignRequest(body),    ct);
        var result = WarehouseHelpers.ParseConsignmentResponse(mb1bData, toNonC, toC);

        // Any leg reporting an SAP error (deficit stock, missing
        // authorization, etc.) means the consignment issue never actually
        // posted — surface it as a real failure instead of 200/success so
        // callers (routes/staging.js's Staging Post delivery flow) don't
        // record a delivery that never happened in SAP.
        if (!result.Success)
        {
            var messages = new List<string>();

            if (!string.IsNullOrWhiteSpace(result.Mb1bMessage))
                messages.Add($"MB1B: {result.Mb1bMessage}");

            if (!string.IsNullOrWhiteSpace(result.ToNonConsignMessage))
                messages.Add($"To Non-Consign: {result.ToNonConsignMessage}");

            if (!string.IsNullOrWhiteSpace(result.ToConsignMessage))
                messages.Add($"To Consign: {result.ToConsignMessage}");

            return UnprocessableEntity(
                ApiResponse<ConsignmentMb1bResponse>.Fail(
                    "422",
                    string.Join(" | ", messages),
                    result));
        }

        return Ok(ApiResponse<ConsignmentMb1bResponse>.Ok(result));
    }

    // ── POST /api/warehouse/set-delivery-weight ───────────────────────────────
    //
    // Transaction ZDEL (program SAPMZDEL, screen 0100) via BDC — records the
    // delivery's actual picked/packed gross weight, net weight (gross minus
    // packaging), and pallet count back onto LIKP once a delivery is marked
    // complete in the pallet builder. Two hits on the same screen exactly as
    // recorded: select the delivery (=SELE), then fill in the weight/pallet
    // fields and save (=SAVE). No CheckPermissionAsync gate, same as the
    // picksheet-* endpoints — called from Node via the shared service token
    // when a delivery is completed, not directly by a logged-in user.

    [HttpPost("set-delivery-weight")]
    [ProducesResponseType(typeof(ApiResponse<SetDeliveryWeightResponse>), 200)]
    public async Task<IActionResult> SetDeliveryWeight(
        [FromBody] SetDeliveryWeightRequest body,
        CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(WarehouseHelpers.BuildZdelRequest(body), ct);
        return Ok(ApiResponse<SetDeliveryWeightResponse>.Ok(WarehouseHelpers.ParseZdelResponse(response)));
    }

    // ── ZDELFLAG/ZDELPACK maintenance (transaction ZPIL9) ─────────────────────
    //
    // Fired after set-delivery-weight when a delivery is marked complete —
    // confirms all materials/packaging assigned to the delivery in SAP's own
    // ZDELFLAG/ZDELPACK tables via the custom BAPI Z_MAINT_ZDELFLAG_ZDELPACK.
    // Node orchestrates: it calls the small lookups below (LIKP~ABLAD,
    // LIPS item detail, KNVV~EIKTO, ZBOM_INFO) to fill in the T_DELFLAG/
    // T_DELPACK rows itself from PalletMain/PalletPackages, then posts the
    // assembled rows to zdelflag/maintain. See ZdelflagHelpers.cs for the
    // full mapping rationale. No CheckPermissionAsync gate, same as the
    // picksheet-*/set-delivery-weight endpoints — called from Node via the
    // shared service token, not directly by a logged-in user.

    [HttpGet("zdelflag/likp-ablad/{delivery}")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    public async Task<IActionResult> GetZdelflagLikpAblad(string delivery, CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(ZdelflagHelpers.BuildLikpAbladRequest(delivery), ct);
        return Ok(ApiResponse<string>.Ok(ZdelflagHelpers.ParseLikpAblad(response)));
    }

    [HttpGet("zdelflag/lips-items/{delivery}")]
    [ProducesResponseType(typeof(ApiResponse<ZdelflagLipsItemRow[]>), 200)]
    public async Task<IActionResult> GetZdelflagLipsItems(string delivery, CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(ZdelflagHelpers.BuildLipsItemDetailRequest(delivery), ct);
        return Ok(ApiResponse<ZdelflagLipsItemRow[]>.Ok(ZdelflagHelpers.ParseLipsItemDetail(response)));
    }

    [HttpGet("zdelflag/eikto/{customer}")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    public async Task<IActionResult> GetZdelflagEikto(string customer, CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(ZdelflagHelpers.BuildKnvvEiktoRequest(customer), ct);
        return Ok(ApiResponse<string>.Ok(ZdelflagHelpers.ParseKnvvEikto(response)));
    }

    [HttpPost("zdelflag/zbom-info")]
    [ProducesResponseType(typeof(ApiResponse<ZbomInfoRow[]>), 200)]
    public async Task<IActionResult> PostZdelflagZbomInfo([FromBody] ZbomInfoRequest body, CancellationToken ct)
    {
        if (body.PackagingInstructions.Count == 0)
            return Ok(ApiResponse<ZbomInfoRow[]>.Ok([]));

        var response = await _pool.ExecuteAsync(ZdelflagHelpers.BuildZbomInfoRequest(body.PackagingInstructions), ct);
        return Ok(ApiResponse<ZbomInfoRow[]>.Ok(ZdelflagHelpers.ParseZbomInfoRows(response)));
    }

    [HttpPost("zdelflag/maintain")]
    [ProducesResponseType(typeof(ApiResponse<MaintainZdelflagResponse>), 200)]
    public async Task<IActionResult> PostZdelflagMaintain([FromBody] MaintainZdelflagRequest body, CancellationToken ct)
    {
        if (body.DelflagRows.Count == 0)
            return Ok(ApiResponse<MaintainZdelflagResponse>.Ok(new MaintainZdelflagResponse("", [])));

        var response = await _pool.ExecuteAsync(ZdelflagHelpers.BuildMaintainRequest(body), ct);
        return Ok(ApiResponse<MaintainZdelflagResponse>.Ok(ZdelflagHelpers.ParseMaintainResponse(response)));
    }

}
