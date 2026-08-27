using System.Net;
using System.Web.Http;
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
[RoutePrefix("api/consignment")]
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
    [HttpGet]
    [Route("gr")]
    public async Task<IHttpActionResult> GetVendorGr(
        [FromUri] string sapVendorNumber,
        // Confirmed for real against this live IIS deploy (2026-08-27): Web
        // API 2's [FromUri] binder 404s the ENTIRE request — not just this
        // parameter — when sinceDate is omitted from the query string
        // entirely, even though it's already string? (nullable reference
        // type). That contradicts this file's own "nullable reference types
        // don't need an explicit default" assumption documented elsewhere in
        // CLAUDE.md for the dryRun-style gotcha — apparently that only holds
        // for a complex [FromUri] object, not a bare nullable string. Every
        // normal caller (Normanton-Nexus's daily cron and its manual "Sync
        // GR from SAP" button) omits sinceDate on every call except a
        // deliberately date-floored re-sync, so this broke ALL consignment
        // GR syncing for every vendor the moment this endpoint's [FromQuery]
        // -> [FromUri] rebuild reached production. The explicit default
        // fixes it the same way the CLAUDE.md-documented value-type cases
        // were fixed.
        [FromUri] string? sinceDate = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sapVendorNumber))
            return Content(HttpStatusCode.BadRequest, ApiResponse<ConsignmentGrRow[]>.Fail("400", "sapVendorNumber is required.", []));

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
    [HttpGet]
    [Route("stock")]
    public async Task<IHttpActionResult> GetConsignmentStock(CancellationToken ct)
    {
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildConsignmentStockRequest(), ct);
        return Ok(ApiResponse<Dictionary<string, decimal>>.Ok(PerformanceHelpers.ParseConsignmentStockRows(response)));
    }
}
