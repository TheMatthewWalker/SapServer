using System.Data;
using System.Globalization;
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

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/stock");

        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildStockRequest(), ct);
        var pc = await _pool.ExecuteAsync(PerformanceHelpers.BuildMaterialProfitCentre(), ct);
        var pcList = PerformanceHelpers.ParseMaterialProfitCentre(pc);
        var rows = PerformanceHelpers.ParseStockRows(response, pcList);

        return Ok(ApiResponse<PerformanceStockRow[]>.Ok(rows));
    }

    // ── GET /api/performance/agreements ──────────────────────────────────

    [HttpGet("agreements")]
    [ProducesResponseType(typeof(ApiResponse<AgreementRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetAgreements([FromQuery] int? horizonDays, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnStockReqList, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/agreements");

        var horizonEnd = DateTime.Today.AddDays(horizonDays ?? 365); // All orders in a 1 year horizon
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

        // Anomaly check: LocalAmount should always equal Amount * rateDict[Currency]
        // (that's literally what ApplyCurrencyConversion just computed), so the
        // implied ratio below should match the dict rate for every row. If it
        // doesn't, something diverged between the rate this row actually used and
        // the rate sitting in rateDict right now — e.g. a different SAP connection-
        // pool worker/session returned UKURS in a different raw format for this
        // row's currency than the one reflected in the log above. Logs only the
        // rows that disagree, with every value in invariant culture, so the next
        // reproduction gives us the exact bad row instead of another data point
        // that looks fine in isolation.
        foreach (var row in rowsWithCur)
        {
            if (row.Amount == 0 || string.IsNullOrEmpty(row.Currency)) continue;

            var expectedRate = rateDict.GetValueOrDefault(row.Currency, 1m);
            var impliedRate  = row.LocalAmount / row.Amount;

            if (Math.Abs(impliedRate - expectedRate) > 0.01m)
            {
                _logger.LogWarning(
                    "LocalAmount anomaly: RefDoc={RefDoc} Currency={Currency} Amount={Amount} " +
                    "expectedRate(dict)={ExpectedRate} impliedRate(LocalAmount/Amount)={ImpliedRate} LocalAmount={LocalAmount}",
                    row.ReferenceDocument, row.Currency,
                    row.Amount.ToString(CultureInfo.InvariantCulture),
                    expectedRate.ToString(CultureInfo.InvariantCulture),
                    impliedRate.ToString(CultureInfo.InvariantCulture),
                    row.LocalAmount.ToString(CultureInfo.InvariantCulture));
            }
        }

        return Ok(ApiResponse<AgreementRow[]>.Ok(rowsWithCur));
    }

    // ── GET /api/performance/invoicing ───────────────────────────────────

    [HttpGet("invoicing")]
    [ProducesResponseType(typeof(ApiResponse<InvoiceRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetInvoicing([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnSaleAnalHist, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/invoicing");

        var fromDate = from ?? DateTime.Today.AddDays(-31); // ensures all dates in a given month are downloaded.
        var toDate   = to ?? DateTime.Today;
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildInvoicingRequest(fromDate, toDate), ct);
        var pc = await _pool.ExecuteAsync(PerformanceHelpers.BuildMaterialProfitCentre(), ct);
        var pcList = PerformanceHelpers.ParseMaterialProfitCentre(pc);
        var rows = PerformanceHelpers.ParseInvoiceRows(response, pcList);

        return Ok(ApiResponse<InvoiceRow[]>.Ok(rows));
    }

    // ── GET /api/performance/otif ─────────────────────────────────────────

    [HttpGet("otif")]
    [ProducesResponseType(typeof(ApiResponse<OtifRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetOtif([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnCustIndexAnal, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "performance/otif");

        var fromDate = from ?? DateTime.Today.AddDays(-31); // ensures all dates in a given month are downloaded.
        var toDate   = to ?? DateTime.Today;
        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildOtifRequest(fromDate, toDate), ct);
        var rows = PerformanceHelpers.ParseOtifRows(response);

        return Ok(ApiResponse<OtifRow[]>.Ok(rows));
    }

    // ── GET /api/performance/turns-valclass ───────────────────────────────
    // Direct port of the mm_turns_valclass.xlsm "get_all" report — stock,
    // valuation, book value, turns/days-in-stock, 13mo consumption history
    // and 13mo demand forecast, one row per material.

    [HttpGet("turns-valclass")]
    [ProducesResponseType(typeof(ApiResponse<TurnsValClassRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetTurnsValClass([FromQuery] TurnsValClassQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnReadTables, ct);
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnStockReqList, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "turns-valclass");

        var plant = string.IsNullOrWhiteSpace(query.Plant) ? PerformanceHelpers.Plant : query.Plant;

        var masterResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildMaterialMasterRequest(query), ct);
        var masterRows = PerformanceHelpers.ParseMaterialMasterRows(masterResp);

        var materials = masterRows
            .Select(r => r.GetValueOrDefault("MATNR")?.ToString() ?? "")
            .Where(m => m.Length > 0)
            .ToArray();

        // Demand forecast is driven by the query's own filters, independent of the master-data materials.
        var forecastResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildDemandForecastRequest(query), ct);
        var forecast = PerformanceHelpers.ParseDemandForecastRows(forecastResp);

        var history  = new Dictionary<string, decimal[]>();
        var movement = new Dictionary<string, PerformanceHelpers.LastMovementInfo>();

        if (materials.Length > 0)
        {
            // No MATNR filter passed to either call — see the comments on
            // BuildConsumptionHistoryRequest/BuildLastMovementRequest in PerformanceHelpers.cs.
            // A batched-calls version of this was tried and reverted: looping dozens of
            // rapid sequential RFC calls here disturbed *other* concurrent requests sharing
            // the same SAP connection-pool worker (SapStaWorker unconditionally marks itself
            // disconnected on any single failed call, forcing an unrelated queued request to
            // reconnect mid-flight). Pulling MVER/S032 unfiltered in one call each avoids that
            // entirely and matches materials in memory instead.
            var historyResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildConsumptionHistoryRequest(plant), ct);
            history = PerformanceHelpers.ParseConsumptionHistoryRows(historyResp);

            var movementResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildLastMovementRequest(plant), ct);
            movement = PerformanceHelpers.ParseLastMovementRows(movementResp);

            // TEMP DIAGNOSTIC — remove once consumption-history-is-zero is confirmed fixed.
            var nonZeroMaterials = history.Count(kv => kv.Value.Any(v => v != 0));
            _logger.LogInformation(
                "ConsumptionHistory diagnostic: requestedMaterials={RequestedCount} " +
                "parsedMaterials={ParsedCount} materialsWithNonZeroHistory={NonZeroCount}",
                materials.Length, history.Count, nonZeroMaterials);
        }

        var rows = PerformanceHelpers.ComputeTurnsRows(masterRows, forecast, history, movement, query.TurnMonths, query.HistoryMode);

        return Ok(ApiResponse<TurnsValClassRow[]>.Ok(rows));
    }

    // ── GET /api/performance/turns-valclass/valuation-classes ─────────────
    // Direct port of Get_T025_T025T_T134 — the "valid new valuation class" catalog for client-side dropdowns.

    [HttpGet("turns-valclass/valuation-classes")]
    [ProducesResponseType(typeof(ApiResponse<ValClassRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetValuationClasses(CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnReadTables, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "turns-valclass/valuation-classes");

        var response = await _pool.ExecuteAsync(PerformanceHelpers.BuildValuationClassCatalogRequest(), ct);
        return Ok(ApiResponse<ValClassRow[]>.Ok(PerformanceHelpers.ParseValuationClassCatalogRows(response)));
    }

    // ── POST /api/performance/turns-valclass/change-valuation-class ───────
    // Direct port of update_val_class. WRITES to SAP: moves stock out to
    // `Order` (MB1A 291), changes the valuation class per material (MM02),
    // then moves stock back in (MB1A 292). All-or-nothing pre-check first —
    // if any requested material fails validation, nothing is posted to SAP.

    [HttpPost("turns-valclass/change-valuation-class")]
    [ProducesResponseType(typeof(ApiResponse<ChangeValuationClassResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> ChangeValuationClass([FromBody] ChangeValuationClassRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PerformanceHelpers.FnCreate, ct);

        //_logger.LogInformation("User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "turns-valclass/change-valuation-class");

        if (string.IsNullOrWhiteSpace(body.Order) || body.Changes.Count == 0)
        {
            const string msg = "An order and at least one material change are required.";
            return UnprocessableEntity(ApiResponse<ChangeValuationClassResponse>.Fail("422", msg,
                new ChangeValuationClassResponse { Success = false, ErrorMessage = msg }));
        }

        var plant = string.IsNullOrWhiteSpace(body.Plant) ? PerformanceHelpers.Plant : body.Plant;

        // Order must belong to the same company code as the plant — update_val_class's own check.
        var ccPlantResp = await _pool.ExecuteAsync(
            ProductionHelpers.SapRT("T001K", ["BUKRS"], [$"BWKEY EQ '{SapPad.Pad(plant, 4)}'"]), ct);
        var companyCode = ProductionHelpers.ParseSingleSapResult(ccPlantResp);

        var ccOrderResp = await _pool.ExecuteAsync(
            ProductionHelpers.SapRT("COAS", ["BUKRS"], [$"AUFNR EQ '{SapPad.Pad(body.Order, 12)}'"]), ct);
        var orderCompanyCode = ProductionHelpers.ParseSingleSapResult(ccOrderResp);

        if (string.IsNullOrEmpty(orderCompanyCode) || orderCompanyCode != companyCode)
        {
            var msg = $"Order {body.Order} does not exist in company code {companyCode}.";
            return UnprocessableEntity(ApiResponse<ChangeValuationClassResponse>.Fail("422", msg,
                new ChangeValuationClassResponse { Success = false, ErrorMessage = msg }));
        }

        // Current master data (val class, stock qty/value, text) for the requested materials.
        var materials  = body.Changes.Select(c => c.Material).Distinct().ToArray();
        var masterResp = await _pool.ExecuteAsync(
            PerformanceHelpers.BuildMaterialMasterRequest(new TurnsValClassQuery { Plant = plant, Materials = materials }), ct);
        var snapshot   = PerformanceHelpers.IndexMaterialMasterRows(PerformanceHelpers.ParseMaterialMasterRows(masterResp));
        var currentValClass = snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.ValuationClass);

        // All-or-nothing pre-check: valid new valuation classes + no stock-take/non-unrestricted stock.
        var mardResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildMardCheckRequest(materials, plant), ct);
        var mardRows = PerformanceHelpers.ParseMardCheckRows(mardResp);
        var errors   = PerformanceHelpers.ValidateValuationClassChanges(body.Changes, currentValClass, mardRows);

        if (errors.Count > 0)
        {
            var msg = string.Join(" ", errors);
            return UnprocessableEntity(ApiResponse<ChangeValuationClassResponse>.Fail("422", msg,
                new ChangeValuationClassResponse { Success = false, ErrorMessage = msg }));
        }

        // Stock lines to move out and back in — non-batch and batch-managed materials separately.
        var nonBatchResp = await _pool.ExecuteAsync(PerformanceHelpers.BuildNonBatchStockRequest(materials, plant), ct);
        var batchResp    = await _pool.ExecuteAsync(PerformanceHelpers.BuildBatchStockRequest(materials, plant), ct);
        var stockLines = PerformanceHelpers.ParseNonBatchStockRows(nonBatchResp)
            .Concat(PerformanceHelpers.ParseBatchStockRows(batchResp))
            .ToList();

        // 1) Move stock out to the order.
        foreach (var line in stockLines)
        {
            var mb1aOut = await _pool.ExecuteAsync(PerformanceHelpers.BuildMb1aRequest("291", body.Order, line), ct);
            _logger.LogInformation("Valuation class change: MB1A 291 {Material} x {Qty} || {Message}",
                line.Material, line.Quantity, ProductionHelpers.ParseBdcResponse(mb1aOut).RawMessage);
        }

        // 2) Suppress the PID-check popup, change valuation class per material, restore the check.
        await _pool.ExecuteAsync(PerformanceHelpers.BuildPidCheckToggleRequest(active: false), ct);

        var mm02Messages = new Dictionary<string, BdcResponse>();
        foreach (var change in body.Changes)
        {
            var mm02 = await _pool.ExecuteAsync(
                PerformanceHelpers.BuildMm02ValuationClassRequest(change.Material, plant, change.NewValuationClass), ct);
            mm02Messages[PerformanceHelpers.NormaliseMaterial(change.Material)] = ProductionHelpers.ParseBdcResponse(mm02);
        }

        await _pool.ExecuteAsync(PerformanceHelpers.BuildPidCheckToggleRequest(active: true), ct);

        // 3) Move stock back in from the order.
        foreach (var line in stockLines)
        {
            var mb1aIn = await _pool.ExecuteAsync(PerformanceHelpers.BuildMb1aRequest("292", body.Order, line), ct);
            _logger.LogInformation("Valuation class change: MB1A 292 {Material} x {Qty} || {Message}",
                line.Material, line.Quantity, ProductionHelpers.ParseBdcResponse(mb1aIn).RawMessage);
        }

        // 4) Build the report — success is Type=S, Class=M3, Number=801 (SAP's "material saved" message).
        var results = new List<ValClassChangeResult>();
        decimal totalChange = 0;

        foreach (var change in body.Changes)
        {
            var key = PerformanceHelpers.NormaliseMaterial(change.Material);
            var ok  = mm02Messages.TryGetValue(key, out var mm02Msg)
                      && mm02Msg.Type == "S" && mm02Msg.MessageClass == "M3" && mm02Msg.MessageNumber == "801";

            snapshot.TryGetValue(key, out var info);
            var oldValClass  = info?.ValuationClass ?? "";
            var stockValue   = info?.StockValue ?? 0m;
            var oldBookValue = stockValue * PerformanceHelpers.BookValueFactor(oldValClass);
            var newBookValue = ok ? stockValue * PerformanceHelpers.BookValueFactor(change.NewValuationClass) : oldBookValue;
            var valueChange  = ok ? newBookValue - oldBookValue : 0m;

            totalChange += valueChange;

            results.Add(new ValClassChangeResult
            {
                Material          = change.Material,
                MaterialText      = info?.MaterialText ?? "",
                Plant             = plant,
                StockQty          = info?.StockQty ?? 0m,
                OldValuationClass = oldValClass,
                NewValuationClass = ok ? change.NewValuationClass : oldValClass,
                OldBookValue      = oldBookValue,
                NewBookValue      = newBookValue,
                ValueChange       = valueChange,
                Success           = ok,
                Message           = mm02Messages.TryGetValue(key, out var m) ? m.Message : ""
            });
        }

        return Ok(ApiResponse<ChangeValuationClassResponse>.Ok(new ChangeValuationClassResponse
        {
            Success          = results.All(r => r.Success),
            TotalValueChange = totalChange,
            Results          = results
        }));
    }
}
