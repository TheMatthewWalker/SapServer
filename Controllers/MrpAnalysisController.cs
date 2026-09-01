using System.Web.Http;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

// Backs Normanton Nexus's MRP Analysis screen — year-on-year consumption/goods-receipt
// trends per material (resolvable per vendor) and two ways to turn a sales outlook into a
// raw-material purchasing number: a %-increase extrapolation against past consumption, and
// a full sales-unit breakdown exploded down through SAP's multi-level BOM to raw materials
// (profit centre 2012). See MrpAnalysisHelper's own comment for the endpoint-to-RFC map.
[RoutePrefix("api/mrp-analysis")]
public sealed class MrpAnalysisController : SapControllerBase
{
    public MrpAnalysisController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<MrpAnalysisController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/mrp-analysis/consumption-by-year ───────────────────────────────
    //
    // Year-on-year consumption totals per material, straight from MVER — reuses
    // PerformanceHelpers.BuildConsumptionHistoryRequest exactly as the daily TurnsValClass
    // sync does (same unfiltered-by-material bulk pull, same 6-fiscal-year net), just parsed
    // with ParseConsumptionHistoryByYear instead of the rolling-36-month parser. No new RFC
    // call — this is the same SAP round trip, read a second way.

    [HttpGet]

    [Route("consumption-by-year")]
    public async Task<IHttpActionResult> GetConsumptionByYear(CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), MrpAnalysisHelper.FnMrpAnalysis, ct);

        var raw = await _pool.ExecuteAsync(PerformanceHelpers.BuildConsumptionHistoryRequest(), ct);
        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(raw);

        return Ok(ApiResponse<ConsumptionByYearRow[]>.Ok(rows));
    }

    // ── GET /api/mrp-analysis/goods-receipt-history ─────────────────────────────
    //
    // Year-on-year goods-receipt (movement 101) quantity per material, resolved per vendor —
    // two bulk reads (MSEG/MKPF goods-receipt lines, EKKO purchase-order vendors), joined and
    // summed in memory by MrpAnalysisHelper.AggregateGoodsReceiptHistory. sinceDate (optional,
    // SAP dd.MM.yyyy) bounds both pulls — omit for a first-ever full-history sync.

    [HttpGet]

    [Route("goods-receipt-history")]
    public async Task<IHttpActionResult> GetGoodsReceiptHistory([FromUri] string? sinceDate = null, CancellationToken ct = default)
    {
        await CheckPermissionAsync(GetUserId(), MrpAnalysisHelper.FnMrpAnalysis, ct);

        var grRaw    = await _pool.ExecuteAsync(MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest(sinceDate), ct);
        var grRows   = MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(grRaw);

        var poRaw    = await _pool.ExecuteAsync(MrpAnalysisHelper.BuildPurchaseOrderVendorsRequest(sinceDate), ct);
        var poVendors = MrpAnalysisHelper.ParsePurchaseOrderVendorRows(poRaw);

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(grRows, poVendors);

        return Ok(ApiResponse<GoodsReceiptHistoryRow[]>.Ok(rows));
    }

    // ── POST /api/mrp-analysis/explode-bom ──────────────────────────────────────
    //
    // Explodes a set of finished goods (each with an expected/forecast quantity) down
    // through SAP's multi-level BOM to raw materials (profit centre 2012), summing every
    // raw material's total requirement across all of them — see MrpAnalysisHelper.ExplodeBom
    // for the level-by-level orchestration. The real SAP calls (bulk BOM explosion, bulk
    // profit-centre classification) are wired in here as delegates; ExplodeBom itself has no
    // pool dependency.
    //
    // Highest-stakes new endpoint in this feature — its output sizes real purchase order
    // quantities. Validate against at least one known real multi-level BOM before trusting it
    // for an actual blanket PO (see this method's own risk note in the implementation plan).

    [HttpPost]

    [Route("explode-bom")]
    public async Task<IHttpActionResult> ExplodeBom([FromBody] BomExplosionRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), MrpAnalysisHelper.FnMrpAnalysis, ct);

        var result = await MrpAnalysisHelper.ExplodeBom(
            body.Items,
            async (materials, ct2) =>
            {
                var raw = await _pool.ExecuteAsync(ProductionHelpers.BuildBomRequestBulk(materials), ct2);
                return ProductionHelpers.ParseBomRows(raw);
            },
            async (materials, ct2) =>
            {
                var raw = await _pool.ExecuteAsync(ProductionHelpers.BuildProfitCentresRequest(new ProfitCentresRequest { Materials = [.. materials] }), ct2);
                return ProductionHelpers.ParseProfitCentreRows(raw)
                    .ToDictionary(r => PerformanceHelpers.NormaliseMaterial(r.Material), r => r.ProfitCentre);
            },
            ct);

        return Ok(ApiResponse<BomExplosionResult>.Ok(result));
    }
}
