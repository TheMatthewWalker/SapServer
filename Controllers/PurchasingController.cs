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
}
