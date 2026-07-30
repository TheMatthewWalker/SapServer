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
    // reversal/SOBKZ=K, joined to MKPF), fetched as two separate RFC calls
    // (one per movement type) and merged here — see
    // ConsignmentHelpers.BuildVendorGrRequest for why: every attempt at
    // filtering on more than one BWART value in a single call (parenthesised
    // OR, literal SQL IN, RFC_READ_TABLE-style IN-opt/value_list) came back
    // with zero rows, so this reverts to the single-value EQ condition
    // that's actually confirmed to work. A `materials` query param sent by
    // an older Node build is harmless here — it's simply not bound/used;
    // the WHERE filter is LIFNR-based again (see BuildVendorGrRequest).
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

        var response101 = await _pool.ExecuteAsync(
            ConsignmentHelpers.BuildVendorGrRequest(sapVendorNumber, "101", sinceDate), ct);
        var response102 = await _pool.ExecuteAsync(
            ConsignmentHelpers.BuildVendorGrRequest(sapVendorNumber, "102", sinceDate), ct);

        var rows101 = ConsignmentHelpers.ParseVendorGrRows(response101);
        var rows102 = ConsignmentHelpers.ParseVendorGrRows(response102);
        var allRows = rows101.Concat(rows102).ToArray();

        // Diagnostic logging (2026-07-30) — kept from earlier debugging so a
        // future empty/unexpected result is diagnosable from the log alone.
        _logger.LogInformation(
            "[consignment-gr-diag] vendor={SapVendorNumber} sinceDate={SinceDate} rows101={Rows101} rows102={Rows102} total={Total} (build marker: two-call-EQ)",
            sapVendorNumber, sinceDate ?? "(none)", rows101.Length, rows102.Length, allRows.Length);

        return Ok(ApiResponse<ConsignmentGrRow[]>.Ok(allRows));
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
