using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

// Vendor Consignment Tracker — see ConsignmentHelpers.cs for the full
// rationale. No CheckPermissionAsync gate on either endpoint, same as the
// picksheet-*/set-delivery-weight endpoints on WarehouseController: these
// are called from Node's own sync job via the shared service token (userId
// 0), not proxied directly to arbitrary logged-in browser users, so the
// Nexus-side department/permission gating in routes/consignment.js is what
// actually protects this — see VENDOR_CONSIGNMENT (Normanton-Nexus) for the
// declaration-confirm step specifically.
[Route("api/consignment")]
public sealed class ConsignmentController : SapControllerBase
{
    public ConsignmentController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<ConsignmentController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/consignment/gr ────────────────────────────────────────────────
    //
    // Consignment goods-receipt lines for one vendor (MSEG BWART=101 or 102
    // reversal/SOBKZ=K, joined to MKPF) — see
    // ConsignmentHelpers.BuildVendorGrRequest.
    [HttpGet("gr")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentGrRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    public async Task<IActionResult> GetVendorGr(
        [FromQuery] string sapVendorNumber,
        [FromQuery] string? sinceDate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sapVendorNumber))
            return BadRequest(ApiResponse<ConsignmentGrRow[]>.Fail("400", "sapVendorNumber is required.", []));

        var response = await _pool.ExecuteAsync(
            ConsignmentHelpers.BuildVendorGrRequest(sapVendorNumber, sinceDate), ct);

        return Ok(ApiResponse<ConsignmentGrRow[]>.Ok(ConsignmentHelpers.ParseVendorGrRows(response)));
    }

    // ── GET /api/consignment/stock ─────────────────────────────────────────────
    //
    // Live consignment stock (MKOL SLABS), plant-wide — deliberately reuses
    // PerformanceHelpers.BuildConsignmentStockRequest/ParseConsignmentStockRows
    // (the same query already proven for MRP) rather than duplicating it. The
    // caller filters the returned per-material dictionary down to the vendor's
    // own material list itself (Node already knows which materials belong to
    // which vendor via dbo.VendorMaterial).
    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<Dictionary<string, decimal>>), 200)]
    public async Task<IActionResult> GetConsignmentStock(CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildConsignmentStockRequest(), ct);
        return Ok(ApiResponse<Dictionary<string, decimal>>.Ok(PerformanceHelpers.ParseConsignmentStockRows(response)));
    }
}
