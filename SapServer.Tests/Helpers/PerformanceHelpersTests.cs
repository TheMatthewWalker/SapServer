using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

// PerformanceHelpers.cs (1465 lines) backs the Stock Turns & Valuation Class
// dashboard — the largest and most business-logic-dense helper in the
// codebase. This suite covers its testable (internal, not private) pure
// logic: NormaliseMaterial, BookValueFactor, currency conversion, and
// ValidateValuationClassChanges' pre-flight checks (the gate in front of an
// irreversible MM02 valuation-class change).
//
// Not covered here (documented gap): ComputeTurnsRows (the big
// recalc_turns/calc_book_value/make_warnings orchestrator) and
// TurnoverCategoryFor/BuildWarning/StockTurnsFor, which are `private` (not
// `internal`) and so aren't reachable even via InternalsVisibleTo — only
// exercisable indirectly through ComputeTurnsRows, which would need a large,
// multi-table fixture (material master + demand forecast + consumption
// history + last-movement + valuation-class catalog, all correlated by
// NormaliseMaterial'd key) to drive meaningfully. The dozens of other
// Build*Request/Parse*Rows pairs in this file follow the exact same
// ZRFC_READ_TABLES WA-delimited pattern already covered extensively
// elsewhere (ProductionHelpers, WarehouseHelpers, CustomsHelpers, etc.) —
// a representative sample, not every one, is covered here.
public class PerformanceHelpersTests
{
    [Fact]
    public void NormaliseMaterial_strips_leading_zeros_only_from_purely_numeric_values()
    {
        Assert.Equal("12345", PerformanceHelpers.NormaliseMaterial("00012345"));
        Assert.Equal("30005R", PerformanceHelpers.NormaliseMaterial("30005R")); // mixed alnum -> unchanged
        Assert.Equal("", PerformanceHelpers.NormaliseMaterial(""));
    }

    [Theory]
    [InlineData("3005", 0.5)]
    [InlineData("7925", 0.5)]
    [InlineData("3006", 0)]
    [InlineData("7927", 0)]
    [InlineData("3000", 1)]
    [InlineData("unknown-class", 1)]
    public void BookValueFactor_maps_valuation_classes_to_their_book_value_multiplier(string valuationClass, double expected)
    {
        Assert.Equal((decimal)expected, PerformanceHelpers.BookValueFactor(valuationClass));
    }

    /// <summary>
    /// ROWCOUNT must be a real integer, not an empty string - same real bug
    /// found (and fixed) in LogisticsHelpers.BuildPicksheetRequest: SAP NCo's
    /// typed RfcDataContainer.SetValue throws RfcTypeConversionException for
    /// "" on an INT4 parameter, confirmed against a live IIS deploy.
    /// </summary>
    [Fact]
    public void BuildMaterialProfitCentre_sends_ROWCOUNT_as_an_integer_not_an_empty_string()
    {
        var request = PerformanceHelpers.BuildMaterialProfitCentre();

        Assert.Equal(0, request.ImportParameters["ROWCOUNT"]);
    }

    [Fact]
    public void ApplyCurrencyConversion_leaves_LocalAmount_equal_to_Amount_when_currency_is_blank()
    {
        var rows = new[] { new AgreementRow { Currency = "", Amount = 100m } };
        var result = PerformanceHelpers.ApplyCurrencyConversion(rows, new Dictionary<string, decimal>());
        Assert.Equal(100m, result[0].LocalAmount);
    }

    [Fact]
    public void ApplyCurrencyConversion_multiplies_by_the_matching_rate()
    {
        var rows = new[] { new AgreementRow { Currency = "EUR", Amount = 100m } };
        var rates = new Dictionary<string, decimal> { ["EUR"] = 0.85m };
        var result = PerformanceHelpers.ApplyCurrencyConversion(rows, rates);
        Assert.Equal(85m, result[0].LocalAmount);
    }

    [Fact]
    public void ApplyCurrencyConversion_defaults_to_a_1_to_1_rate_when_no_rate_was_found_for_the_currency()
    {
        var rows = new[] { new AgreementRow { Currency = "XYZ", Amount = 100m } };
        var result = PerformanceHelpers.ApplyCurrencyConversion(rows, new Dictionary<string, decimal>());
        Assert.Equal(100m, result[0].LocalAmount);
    }

    [Fact]
    public void BuildCurrencyRequests_skips_blank_currency_codes()
    {
        var requests = PerformanceHelpers.BuildCurrencyRequests(["EUR", "", "USD"], "GBP").ToList();
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public void ParseCurrencyRows_trims_the_currency_code_and_parses_the_European_decimal_rate()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["CURR_RATE_T"] = [new() { ["FCURR"] = "EUR ", ["UKURS"] = "1,17500" }] },
        };
        var rates = PerformanceHelpers.ParseCurrencyRows(response);
        Assert.Equal(1.175m, rates["EUR"]);
    }

    private static Dictionary<string, string> CurrentClasses() => new() { ["30005R"] = "3000" };

    [Fact]
    public void ValidateValuationClassChanges_rejects_an_unrecognised_target_class()
    {
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "9999" }],
            CurrentClasses(), []);

        Assert.Contains(errors, e => e.Contains("not a recognised valuation class"));
    }

    [Fact]
    public void ValidateValuationClassChanges_rejects_a_material_not_in_the_plant_master_data()
    {
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "UNKNOWN", NewValuationClass = "3005" }],
            CurrentClasses(), []);

        Assert.Contains(errors, e => e.Contains("material not found in plant master data"));
    }

    [Fact]
    public void ValidateValuationClassChanges_rejects_a_no_op_change_to_the_same_class()
    {
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3000" }],
            CurrentClasses(), []);

        Assert.Contains(errors, e => e.Contains("same as the current one"));
    }

    [Fact]
    public void ValidateValuationClassChanges_accepts_a_genuinely_different_recognised_class()
    {
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3005" }],
            CurrentClasses(), []);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateValuationClassChanges_blocks_a_material_with_an_active_stock_take()
    {
        var mard = new PerformanceHelpers.MardCheckRow("30005R", "1000", "X", 100, 0, 0, 0, 0, 0);
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3005" }],
            CurrentClasses(), [mard]);

        Assert.Contains(errors, e => e.Contains("stock take is active"));
    }

    [Fact]
    public void ValidateValuationClassChanges_blocks_a_material_with_any_non_unrestricted_stock()
    {
        var mard = new PerformanceHelpers.MardCheckRow("30005R", "1000", "", 100, 0, 5, 0, 0, 0); // 5 in quality inspection
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3005" }],
            CurrentClasses(), [mard]);

        Assert.Contains(errors, e => e.Contains("not all stock is unrestricted"));
    }

    [Fact]
    public void ValidateValuationClassChanges_ignores_MARD_rows_for_materials_that_were_not_requested()
    {
        var mard = new PerformanceHelpers.MardCheckRow("OTHERMAT", "1000", "X", 100, 0, 0, 0, 0, 0);
        var errors = PerformanceHelpers.ValidateValuationClassChanges(
            [new ValClassChangeItem { Material = "30005R", NewValuationClass = "3005" }],
            CurrentClasses(), [mard]);

        Assert.Empty(errors);
    }

    // ParseConsumptionHistoryByYear — reuses the exact same MVER data_display rows as
    // ParseConsumptionHistoryRows (see that method's fixture shape in production code),
    // just summed per (Material, GJAHR) instead of windowed into a rolling 36-month array.
    private static RfcResponse MverResponse(params string[] dataRows)
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["WA"] = "MATNR|WERKS|GJAHR|GSV01|GSV02|GSV03|GSV04|GSV05|GSV06|GSV07|GSV08|GSV09|GSV10|GSV11|GSV12" } };
        rows.AddRange(dataRows.Select(r => new Dictionary<string, object?> { ["WA"] = r }));
        return new RfcResponse { Tables = new() { ["data_display"] = rows } };
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_sums_all_12_periods_for_one_material_year()
    {
        var response = MverResponse("30005R|3012|2025|10|20|30|40|50|60|70|80|90|100|110|120");

        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(response);

        var row = Assert.Single(rows);
        Assert.Equal("30005R", row.Material);
        Assert.Equal(2025, row.FiscalYear);
        Assert.Equal(780m, row.Qty); // 10+20+...+120
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_keeps_a_purely_numeric_material_zero_padded_not_normalised()
    {
        // log.MaterialConsumptionHistory (Normanton-Nexus) is joined against
        // log.TurnsValClassSnapshot.Material, which stores MATNR exactly as
        // ComputeTurnsRows outputs it — raw/padded, never NormaliseMaterial'd. Stripping
        // leading zeros here would silently break that join for any purely-numeric material
        // (a mixed alnum code like "30005R" survives either way, which is why the other
        // tests in this class don't catch this).
        var response = MverResponse("000000000000100234|3012|2025|1|1|1|1|1|1|1|1|1|1|1|1");

        var row = Assert.Single(PerformanceHelpers.ParseConsumptionHistoryByYear(response));

        Assert.Equal("000000000000100234", row.Material);
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_returns_one_row_per_material_per_fiscal_year_not_a_rolling_window()
    {
        var response = MverResponse(
            "30005R|3012|2024|1|1|1|1|1|1|1|1|1|1|1|1", // 12
            "30005R|3012|2025|2|2|2|2|2|2|2|2|2|2|2|2", // 24
            "30006R|3012|2025|3|3|3|3|3|3|3|3|3|3|3|3"  // 36
        );

        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(response);

        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, r => r.Material == "30005R" && r.FiscalYear == 2024 && r.Qty == 12m);
        Assert.Contains(rows, r => r.Material == "30005R" && r.FiscalYear == 2025 && r.Qty == 24m);
        Assert.Contains(rows, r => r.Material == "30006R" && r.FiscalYear == 2025 && r.Qty == 36m);
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_accumulates_split_valuated_duplicate_rows_for_the_same_material_year()
    {
        // Same material/year appearing twice (e.g. split valuation) should add together,
        // not overwrite — same "accumulate, don't clobber" expectation as the rest of this
        // file's dedupe-aware parsing.
        var response = MverResponse(
            "30005R|3012|2025|1|1|1|1|1|1|1|1|1|1|1|1", // 12
            "30005R|3012|2025|1|1|1|1|1|1|1|1|1|1|1|1"  // 12 again
        );

        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(response);

        var row = Assert.Single(rows);
        Assert.Equal(24m, row.Qty);
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_skips_a_row_with_too_few_columns()
    {
        var response = MverResponse("30005R|3012|2025|only|four|cols");

        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(response);

        Assert.Empty(rows);
    }

    [Fact]
    public void ParseConsumptionHistoryByYear_returns_empty_when_there_is_no_data_display_table()
    {
        var rows = PerformanceHelpers.ParseConsumptionHistoryByYear(new RfcResponse());
        Assert.Empty(rows);
    }
}
