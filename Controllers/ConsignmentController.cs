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

        // Temporary diagnostic logging (2026-07-30) — the BWART IN ('101','102')
        // fix (cf5acc2) still came back pulled=0 on a live re-sync after
        // rebuild, same as the OR/parens attempt before it. Logging the raw
        // row count vs. the post-filter count separates two very different
        // failure modes: rawRowCount=0 means SAP itself matched nothing
        // (WHERE/vendor/plant issue, or this build still isn't the one
        // running); rawRowCount>0 but parsedRowCount=0 means SAP returned
        // rows but ParseVendorGrRows's `cols.Length >= expectedCols` filter
        // is dropping every one of them (a delimited-column-count mismatch —
        // plausible since SHKZG was just added as a 10th expected column).
        // Remove once the sync is confirmed working again.
        var rawRowCount = response.Tables.TryGetValue("data_display", out var rawRows) ? rawRows.Count : -1;
        var parsedRows  = ConsignmentHelpers.ParseVendorGrRows(response);
        _logger.LogInformation(
            "[consignment-gr-diag] vendor={SapVendorNumber} sinceDate={SinceDate} rawRowCount={RawRowCount} parsedRowCount={ParsedRowCount} (build marker: BWART-IN)",
            sapVendorNumber, sinceDate ?? "(none)", rawRowCount, parsedRows.Length);

        return Ok(ApiResponse<ConsignmentGrRow[]>.Ok(parsedRows));
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
