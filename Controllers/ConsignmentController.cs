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
    // ConsignmentHelpers.BuildVendorGrRequest. materials (comma-separated
    // MATNR list, Node's dbo.VendorMaterial for this vendor) is required and
    // is the primary/selective WHERE filter, not just an LIFNR safety net —
    // see BuildVendorGrRequest's comment for why (LIFNR filtering alone was
    // too slow in production, 2026-07-30).
    [HttpGet("gr")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentGrRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    public async Task<IActionResult> GetVendorGr(
        [FromQuery] string sapVendorNumber,
        [FromQuery] string? materials,
        [FromQuery] string? sinceDate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sapVendorNumber))
            return BadRequest(ApiResponse<ConsignmentGrRow[]>.Fail("400", "sapVendorNumber is required.", []));

        var materialList = (materials ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (materialList.Length == 0)
            return BadRequest(ApiResponse<ConsignmentGrRow[]>.Fail("400",
                "materials is required (comma-separated MATNR list) — see ConsignmentHelpers.BuildVendorGrRequest.", []));

        var response = await _pool.ExecuteAsync(
            ConsignmentHelpers.BuildVendorGrRequest(sapVendorNumber, materialList, sinceDate), ct);

        // Temporary diagnostic logging (2026-07-30, kept through the
        // MATNR-filter change) — logs raw SAP row count vs. post-filter
        // count so a future empty/slow result is diagnosable from the log
        // alone rather than another blind guess-and-rebuild cycle.
        var rawRowCount = response.Tables.TryGetValue("data_display", out var rawRows) ? rawRows.Count : -1;
        var parsedRows  = ConsignmentHelpers.ParseVendorGrRows(response);
        _logger.LogInformation(
            "[consignment-gr-diag] vendor={SapVendorNumber} materialCount={MaterialCount} sinceDate={SinceDate} rawRowCount={RawRowCount} parsedRowCount={ParsedRowCount} (build marker: MATNR-filter)",
            sapVendorNumber, materialList.Length, sinceDate ?? "(none)", rawRowCount, parsedRows.Length);

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
