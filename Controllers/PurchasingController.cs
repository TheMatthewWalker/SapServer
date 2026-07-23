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
    /// Posts a goods receipt against a purchase order via transaction MB01
    /// (BDC recording) — see GoodsReceiptHelper for the exact recording
    /// this was built from. BAPI_GOODSMVT_CREATE was tried first but
    /// doesn't work against this SAP system, per the user, hence the BDC
    /// approach instead. A plain BDC call via Z_RFC_CALL_TRANSACTION
    /// commits itself within the transaction it drives, so unlike
    /// CreatePurchaseOrder this doesn't need a pinned worker or an
    /// explicit BAPI_TRANSACTION_COMMIT/ROLLBACK — same as the existing
    /// set-delivery-weight (ZDEL) and consignment (MB1B) BDC endpoints.
    /// </summary>
    [HttpPost("post-goods-receipt")]
    [ProducesResponseType(typeof(ApiResponse<GoodsReceiptResponse>), 200)]
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
        var response = GoodsReceiptHelper.ParseGoodsReceiptResponse(data);

        return Ok(ApiResponse<GoodsReceiptResponse>.Ok(response));
    }
}
