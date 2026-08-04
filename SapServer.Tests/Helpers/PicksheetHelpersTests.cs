using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class PicksheetHelpersTests
{
    [Fact]
    public void BuildStockRequest_filters_LQUA_by_the_given_material_list()
    {
        var request = PicksheetHelpers.BuildStockRequest(new PicksheetStockRequest { Materials = ["30005R"] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("LQUA~MATNR IN opt", whereText);
        Assert.Equal("30005R", request.InputTablesItems["value_list"][0]["LOW"]); // mixed alnum -> SapPad leaves it unchanged
    }

    [Fact]
    public void ParseStockRows_maps_the_joined_LQUA_plus_ZPRODBATCH_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header|row|skipped|here|too|a|b|c|d|e|f" },
                    new() { ["WA"] = "30005R|BATCH1|PDR|B01|100|80|K|SOBKZ|VENDOR1|IB_363660_MB|0080001234" },
                }
            }
        };

        var rows = PicksheetHelpers.ParseStockRows(response);

        Assert.Single(rows);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal("IB_363660_MB", rows[0].PackagingMaterial);
        Assert.Equal("0080001234", rows[0].AllocatedDelivery);
    }

    [Fact]
    public void BuildLipsRequest_filters_on_delivery_quantity_LFIMG_not_confirmed_quantity_KCMENG()
    {
        // A picksheet is, by definition, a delivery that hasn't been picked
        // yet — KCMENG (confirmed qty) is always 0 at this stage, which is
        // exactly why this uses LFIMG (the quantity on the delivery itself)
        // instead, unlike CustomsHelpers.BuildLipsRequest's KCMENG filter.
        var request = PicksheetHelpers.BuildLipsRequest(new PicksheetLipsRequest { Deliveries = ["80001234"] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("LIPS~LFIMG > 0", whereText);
        Assert.DoesNotContain("KCMENG", whereText);
    }

    [Fact]
    public void BuildBatchSnapshotRequest_filters_to_the_exact_material_and_batch()
    {
        var request = PicksheetHelpers.BuildBatchSnapshotRequest("30005r", "batch1");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("LQUA~MATNR EQ '30005r'", whereText); // mixed-alnum -> SapPad leaves it unchanged
    }

    [Fact]
    public void ParseBatchSnapshot_returns_the_first_matching_row()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header|only|here|too|five|six" },
                    new() { ["WA"] = "30005R|BATCH1|PDR|B01|1000|100,000" },
                }
            }
        };

        var snapshot = PicksheetHelpers.ParseBatchSnapshot(response);

        Assert.NotNull(snapshot);
        Assert.Equal("30005R", snapshot!.Material);
        Assert.Equal("PDR", snapshot.StorageType);
        Assert.Equal(100.000m, snapshot.TotalQty); // "100,000" -> European format, no thousands-sep collision here
    }

    [Fact]
    public void ParseBatchSnapshot_returns_null_when_nothing_matches()
    {
        Assert.Null(PicksheetHelpers.ParseBatchSnapshot(new RfcResponse()));
    }

    [Fact]
    public void BinExists_is_true_only_when_the_lookup_returns_at_least_one_row()
    {
        var found = new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGTYP" }, new() { ["WA"] = "916" } } } };
        var notFound = new RfcResponse { Tables = new() };

        Assert.True(PicksheetHelpers.BinExists(found));
        Assert.False(PicksheetHelpers.BinExists(notFound));
    }

    [Fact]
    public void BuildCreateBinRequest_stages_into_storage_type_916_section_001()
    {
        var request = PicksheetHelpers.BuildCreateBinRequest("0080001234");
        var rows = request.InputTablesItems["BDCTABLE"];

        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "LAGP-LGTYP" && (string?)r["FVAL"] == "916");
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "LAGP-LGPLA" && (string?)r["FVAL"] == "0080001234");
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "LAGP-LGBER" && (string?)r["FVAL"] == "001");
    }

    private static PicksheetHelpers.BatchSnapshotRow Snapshot(string storageType, string bin) =>
        new(Material: "30005R", Batch: "BATCH1", StorageType: storageType, Bin: bin, StorageLocation: "1000", TotalQty: 50m);

    [Fact]
    public void ShouldReverse_is_true_only_when_the_batch_is_still_sitting_in_the_exact_staged_bin()
    {
        Assert.True(PicksheetHelpers.ShouldReverse(Snapshot("916", "0080001234"), "0080001234"));
    }

    [Fact]
    public void ShouldReverse_is_false_when_the_batch_has_moved_out_of_the_staging_storage_type()
    {
        Assert.False(PicksheetHelpers.ShouldReverse(Snapshot("PDR", "0080001234"), "0080001234"));
    }

    [Fact]
    public void ShouldReverse_is_false_when_the_bin_no_longer_matches_what_was_staged()
    {
        Assert.False(PicksheetHelpers.ShouldReverse(Snapshot("916", "0080009999"), "0080001234"));
    }

    [Fact]
    public void ShouldReverse_is_false_when_there_is_no_snapshot_at_all_nothing_to_reverse()
    {
        Assert.False(PicksheetHelpers.ShouldReverse(null, "0080001234"));
    }

    [Fact]
    public void BuildUnstageTransferOrderBody_moves_stock_from_the_staging_bin_back_to_its_original_source()
    {
        var snapshot = Snapshot("916", "0080001234");
        var body = PicksheetHelpers.BuildUnstageTransferOrderBody(snapshot, "PDR", "B01");

        Assert.Equal("916", body.SourceType);
        Assert.Equal("0080001234", body.SourceBin);
        Assert.Equal("PDR", body.DestinationType);
        Assert.Equal("B01", body.DestinationBin);
        Assert.Equal(50m, body.Quantity);
    }
}
