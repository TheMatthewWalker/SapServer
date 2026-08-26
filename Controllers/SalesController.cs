using System.Web.Http;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

// Pure SAP RFC gateway — mirrors PerformanceController's live-query shape
// (no DB access here; Normanton-Nexus is responsible for any caching). Backs
// the sales-page "Schedule Agreement Waterfall" tool, a rebuild of
// N:\Config\Office\Templates\AT_46c\sd_waterfall.xltm — see that plan's
// Context section for the full reverse-engineering trail from the legacy
// tool's exported VBA module (get_data.bas).
[RoutePrefix("api/sales")]
public sealed class SalesController : SapControllerBase
{
    public SalesController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<SalesController> logger)
        : base(pool, permissions, logger) { }

    // ── POST /api/sales/schedule-waterfall ───────────────────────────────

    [HttpPost]

    [Route("schedule-waterfall")]
    public async Task<IHttpActionResult> ScheduleWaterfall([FromBody] ScheduleWaterfallRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SalesOrg) || request.ShipToParties.Count == 0)
            return Ok(ApiResponse<ScheduleWaterfallRow[]>.Ok([]));

        await CheckPermissionAsync(GetUserId(), SalesHelpers.FnReadTables, ct);

        var historyResponse = await _pool.ExecuteAsync(SalesHelpers.BuildHistoryRequest(request), ct);
        var currentResponse = await _pool.ExecuteAsync(SalesHelpers.BuildCurrentRequest(request), ct);

        var historyRows = SalesHelpers.ParseRows(historyResponse, isCurrent: false);
        var currentRows = SalesHelpers.ParseRows(currentResponse, isCurrent: true);

        var rows = SalesHelpers.ComputeCumulativeRelease(historyRows.Concat(currentRows));

        return Ok(ApiResponse<ScheduleWaterfallRow[]>.Ok(rows));
    }
}
