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
}
