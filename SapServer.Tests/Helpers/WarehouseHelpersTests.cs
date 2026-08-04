using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class WarehouseHelpersTests
{
    private static RfcResponse StockResponse(params string[] dataRows)
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["WA"] = "header|row|skipped" } };
        rows.AddRange(dataRows.Select(r => new Dictionary<string, object?> { ["WA"] = r }));
        return new RfcResponse { Tables = new() { ["data_display"] = rows } };
    }

    [Fact]
    public void ParseStockRows_maps_LQUA_columns_in_order()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|12.5|BATCH1|F|Q|SO123");

        var rows = WarehouseHelpers.ParseStockRows(response);

        Assert.Single(rows);
        Assert.Equal("1710", rows[0].StorageLocation);
        Assert.Equal("SA", rows[0].StorageType);
        Assert.Equal("BIN-001", rows[0].Bin);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal(12.5m, rows[0].AvailableQty);
        Assert.Equal("BATCH1", rows[0].Batch);
    }

    [Fact]
    public void ParseStockRows_unparsable_quantity_defaults_to_zero_rather_than_throwing()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|not-a-number|BATCH1|F|Q|SO123");
        var rows = WarehouseHelpers.ParseStockRows(response);
        Assert.Equal(0m, rows[0].AvailableQty);
    }

    [Fact]
    public void AggregateByMaterial_sums_quantity_and_counts_quants_per_material_sorted_alphabetically()
    {
        var stock = new[]
        {
            new StockRow { Material = "30006R", AvailableQty = 5m },
            new StockRow { Material = "30005R", AvailableQty = 10m },
            new StockRow { Material = "30005R", AvailableQty = 2.5m },
        };

        var totals = WarehouseHelpers.AggregateByMaterial(stock);

        Assert.Equal(2, totals.Length);
        Assert.Equal("30005R", totals[0].Material); // alphabetical, not insertion order
        Assert.Equal(12.5m, totals[0].TotalQty);
        Assert.Equal(2, totals[0].QuantCount);
        Assert.Equal("30006R", totals[1].Material);
    }

    [Fact]
    public void AggregateByBin_groups_by_storage_type_and_bin_together_not_bin_alone()
    {
        var stock = new[]
        {
            new StockRow { StorageType = "SA", Bin = "BIN-001", AvailableQty = 5m },
            new StockRow { StorageType = "SB", Bin = "BIN-001", AvailableQty = 3m }, // same bin, different type
        };

        var bins = WarehouseHelpers.AggregateByBin(stock);

        Assert.Equal(2, bins.Length); // must NOT collapse into one "BIN-001" row
    }

    [Fact]
    public void ParseTransferOrderResponse_reads_the_TO_number_and_RETURN_messages()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["E_TANUM"] = "0000001234" },
            Tables = new()
            {
                ["RETURN"] = new()
                {
                    new() { ["TYPE"] = "S", ["MESSAGE"] = "Transfer order 1234 created" },
                },
            },
        };

        var result = WarehouseHelpers.ParseTransferOrderResponse(response);

        Assert.Equal("0000001234", result.TransferOrderNumber);
        Assert.True(result.Success);
        Assert.Single(result.Messages);
        Assert.Equal("S", result.Messages[0].Type);
    }

    private static RfcResponse Mb1bMessage(string type, string message) => new()
    {
        Parameters = new() { ["MESSG"] = $"{type}    M7   001 {message}" },
    };

    [Fact]
    public void ParseConsignmentResponse_succeeds_when_all_three_legs_report_a_non_error_type()
    {
        var result = WarehouseHelpers.ParseConsignmentResponse(
            Mb1bMessage("S", "MB1B posted"),
            Mb1bMessage("S", "Moved to non-consign"),
            Mb1bMessage("S", "Moved to consign"));

        Assert.True(result.Success);
        Assert.Equal("S    M7   001 MB1B posted", result.Mb1bMessage);
    }

    // Regression test: previously ParseConsignmentResponse only kept the raw
    // MESSG text and never looked at the message type, so a rejected MB1B
    // (deficit stock, missing authorization, etc.) was indistinguishable
    // from a successful one to WarehouseController.ConsignmentMb1b — the
    // consignment stock never actually left SAP even though the endpoint
    // reported success.
    [Fact]
    public void ParseConsignmentResponse_fails_when_the_MB1B_leg_reports_an_SAP_error()
    {
        var result = WarehouseHelpers.ParseConsignmentResponse(
            Mb1bMessage("E", "Deficit of SL stock 5 PC : 30005R 1000 SA B02"),
            Mb1bMessage("S", "Moved to non-consign"),
            Mb1bMessage("S", "Moved to consign"));

        Assert.False(result.Success);
    }

    [Fact]
    public void ParseConsignmentResponse_fails_when_either_LT01_leg_reports_an_SAP_error()
    {
        var toNonConsignFails = WarehouseHelpers.ParseConsignmentResponse(
            Mb1bMessage("S", "MB1B posted"),
            Mb1bMessage("E", "Bin does not exist"),
            Mb1bMessage("S", "Moved to consign"));
        Assert.False(toNonConsignFails.Success);

        var toConsignFails = WarehouseHelpers.ParseConsignmentResponse(
            Mb1bMessage("S", "MB1B posted"),
            Mb1bMessage("S", "Moved to non-consign"),
            Mb1bMessage("E", "Bin does not exist"));
        Assert.False(toConsignFails.Success);
    }

    [Fact]
    public void BinExists_is_false_when_only_the_SAP_header_row_comes_back()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPLA" } } }, // header only, no real hit
        };
        Assert.False(WarehouseHelpers.BinExists(response));
    }

    [Fact]
    public void BinExists_is_true_when_a_real_row_follows_the_header()
    {
        var response = StockResponse("BIN-001");
        Assert.True(WarehouseHelpers.BinExists(response));
    }

    [Fact]
    public void IsQualityBlocked_is_true_only_for_stock_category_Q()
    {
        Assert.True(WarehouseHelpers.IsQualityBlocked(StockResponse("Q")));
        Assert.False(WarehouseHelpers.IsQualityBlocked(StockResponse("F")));
        Assert.False(WarehouseHelpers.IsQualityBlocked(new RfcResponse()));
    }

    [Fact]
    public void BuildStockRequest_only_adds_optional_WHERE_conditions_that_were_actually_supplied()
    {
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { Material = "30005R" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~LGNUM EQ '312'", whereText);
        Assert.Contains("LQUA~MATNR", whereText);
        Assert.DoesNotContain("LGTYP", whereText);
        Assert.DoesNotContain("LGPLA", whereText);
    }
}
