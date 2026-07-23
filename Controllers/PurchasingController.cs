using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/purchasing")]
public sealed class PurchasingController : SapControllerBase
{
    public PurchasingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<PurchasingController> logger)
        : base(pool, permissions, logger) { }

    /// <summary>
    /// Creates a Purchase Order via BAPI_PO_CREATE1 (one PO, one PO item per
    /// request item) — see PurchasingHelper for the VBA source this was
    /// ported from. This is PO creation only; it does not book a goods
    /// receipt / GRNI entry. That step (MIGO) is separate, not-yet-built
    /// work, and is deliberately out of scope here.
    ///
    /// Uses a pinned worker (AcquireWorker/ExecuteOnWorkerAsync) so the
    /// commit/rollback that follows runs on the same SAP session as the
    /// BAPI call, matching the pattern already established in
    /// CostingController.PostFreight for BAPI_ACC_DOCUMENT_POST.
    /// </summary>
    [HttpPost("create-po")]
    [ProducesResponseType(typeof(ApiResponse<PoCreateRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> CreatePurchaseOrder(
        [FromBody] PoCreateRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PurchasingHelper.FnPoCreate, ct);

        var request = PurchasingHelper.BuildPoCreateRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var worker = _pool.AcquireWorker();

        var data = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
        var response = PurchasingHelper.ParsePoCreateResult(data);

        if (!response.Success)
        {
            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return BadRequest(ApiResponse<PoCreateRow>.Fail("INVALID_DATA", "Purchase order creation failed. Transaction rolled back.", response));
        }

        await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
        return Ok(ApiResponse<PoCreateRow>.Ok(response));
    }

    /// <summary>
    /// Posts a goods receipt against a single purchase order item via
    /// transaction MB01 (BDC recording) — see GoodsReceiptHelper for the
    /// exact recording this was built from. BAPI_GOODSMVT_CREATE was tried
    /// first but doesn't work against this SAP system, per the user, hence
    /// the BDC approach instead. A plain BDC call via Z_RFC_CALL_TRANSACTION
    /// commits itself within the transaction it drives, so unlike
    /// CreatePurchaseOrder this doesn't need a pinned worker or an
    /// explicit BAPI_TRANSACTION_COMMIT/ROLLBACK — same as the existing
    /// set-delivery-weight (ZDEL) and consignment (MB1B) BDC endpoints.
    ///
    /// One call per PO item/cost line, per the user — the caller loops this
    /// once per cost line on the shipment's PO, incrementing LineNumber each
    /// time. Reuses ProductionHelpers.ParseBdcResponse (already built for
    /// the MF41/MBST BDC endpoints) rather than a bespoke parser — its
    /// DocumentNumber field is exactly what's needed to store the material
    /// document per cost line for later individual reversal.
    /// </summary>
    [HttpPost("post-goods-receipt")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> PostGoodsReceipt(
        [FromBody] GoodsReceiptRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), GoodsReceiptHelper.TransactionCode, ct);

        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var data = await _pool.ExecuteAsync(request, ct);
        var response = ProductionHelpers.ParseBdcResponse(data);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }

    /// <summary>
    /// Reverses a single goods receipt material document via transaction
    /// MBST — reuses the existing MBST BDC already built for scrap
    /// reversal (ProductionHelpers.BuildMbstRequest / POST
    /// /api/production/scrap/reverse) unchanged. That BDC's account-
    /// assignment confirmation screens (SAPLKACB 0002, plain =ENTE with no
    /// re-entered cost object) already match our scenario: our PO items are
    /// cost-center-assigned exactly like the scrap postings it was built
    /// for, and on a reversal SAP already knows the account assignment from
    /// the original document, so no new fields are needed here — just the
    /// material document number of the one cost line's GR being reversed
    /// (see PostGoodsReceipt/BdcResponse.DocumentNumber).
    /// </summary>
    [HttpPost("reverse-goods-receipt")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ReverseGoodsReceipt(
        [FromBody] Mf41Request body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var request = ProductionHelpers.BuildMbstRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var data = await _pool.ExecuteAsync(request, ct);
        var response = ProductionHelpers.ParseBdcResponse(data);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }
}
