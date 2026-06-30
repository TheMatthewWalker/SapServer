using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Services;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

// Pure SAP RFC gateway — no database access. Pulls fresh data from SAP and
// returns it as JSON; Node is responsible for caching it into SQL Server and
// for computing DockStockAllocated / PickedStockAllocated on AgreementRow.
[Route("api/performance")]
public sealed class PerformanceController : SapControllerBase
{
    public PerformanceController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<PerformanceController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/performance/stock ───────────────────────────────────────

    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<PerformanceStockRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStock(CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnReadTables, ct);

        _logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/stock");

        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildStockRequest(), ct);
        var rows = PerformanceHelpers.ParseStockRows(response);

        return Ok(ApiResponse<PerformanceStockRow[]>.Ok(rows));
    }

    // ── GET /api/performance/agreements ──────────────────────────────────

    [HttpGet("agreements")]
    [ProducesResponseType(typeof(ApiResponse<AgreementRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetAgreements([FromQuery] int? horizonDays, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnStockReqList, ct);

        _logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/agreements");

        var horizonEnd = DateTime.Today.AddDays(horizonDays ?? 365); // 365 matches the VBA's Now()+365
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildAgreementsRequest(horizonEnd), ct);

        var rc = ReturnTableHelper.GetParam(response, "RC");
        if (rc == "4")
            _logger.LogInformation("Z_STOCK_REQ_LIST returned RC=4 (no data for selection) for user {UserId}.", GetUserId());

        var rows = PerformanceHelpers.ParseAgreementRows(response);
        var currencies = rows.Select(r => r.Currency).Distinct();
        var localCurrency = rows.FirstOrDefault()?.LocalCurrency ?? "GBP";
        var rateDict = new Dictionary<string, decimal>();

        foreach (var reqCur in PerformanceHelpers.BuildCurrencyRequests(currencies, localCurrency))
        {   var res = await _pool.ExecuteAsync(reqCur, ct);
            var parsed = PerformanceHelpers.ParseCurrencyRows(res);
            foreach (var kv in parsed)
                rateDict[kv.Key] = kv.Value;
        }

        var rowsWithCur = PerformanceHelpers.ApplyCurrencyConversion(rows, rateDict);

        return Ok(ApiResponse<AgreementRow[]>.Ok(rowsWithCur));
    }

    // ── GET /api/performance/invoicing ───────────────────────────────────

    [HttpGet("invoicing")]
    [ProducesResponseType(typeof(ApiResponse<InvoiceRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetInvoicing([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnSaleAnalHist, ct);

        _logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/invoicing");

        var fromDate = from ?? DateTime.Today.AddDays(-30); // 1000 days matches the VBA's Now()-1000
        var toDate   = to ?? DateTime.Today;
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildInvoicingRequest(fromDate, toDate), ct);
        var rows = PerformanceHelpers.ParseInvoiceRows(response);

        return Ok(ApiResponse<InvoiceRow[]>.Ok(rows));
    }

    // ── GET /api/performance/otif ─────────────────────────────────────────

    [HttpGet("otif")]
    [ProducesResponseType(typeof(ApiResponse<OtifRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetOtif([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnCustIndexAnal, ct);

        _logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/otif");

        var fromDate = from ?? DateTime.Today.AddDays(-365); // 365 matches the VBA's DateValue(Now())-365
        var toDate   = to ?? DateTime.Today;
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildOtifRequest(fromDate, toDate), ct);
        var rows = PerformanceHelpers.ParseOtifRows(response);

        return Ok(ApiResponse<OtifRow[]>.Ok(rows));
    }
}
