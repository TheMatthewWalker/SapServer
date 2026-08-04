using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class QualityHelpersTests
{
    [Fact]
    public void BuildBlockedStockRequest_always_filters_to_the_block_indicator_and_warehouse()
    {
        var request = QualityHelpers.BuildBlockedStockRequest(new StockQuery());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~LGNUM EQ '312'", whereText);
        Assert.Contains("LQUA~BESTQ EQ 'S'", whereText);
    }

    [Fact]
    public void BuildBlockedStockRequest_adds_optional_filters_only_when_given()
    {
        var request = QualityHelpers.BuildBlockedStockRequest(new StockQuery { Material = "12345678", StorageType = "PDR", Bin = "B01", Batch = "BATCH1" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~MATNR EQ '000000000012345678'", whereText); // purely numeric -> SapPad zero-pads to 18
        Assert.Contains("LQUA~LGTYP EQ 'PDR'", whereText);
        Assert.Contains("LQUA~LGPLA EQ 'B01'", whereText);
        Assert.Contains("LQUA~CHARG EQ 'BATCH1'", whereText);
    }

    [Fact]
    public void BuildBlockedStockRequest_omits_optional_filters_when_not_given()
    {
        var request = QualityHelpers.BuildBlockedStockRequest(new StockQuery());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.DoesNotContain("MATNR", whereText);
        Assert.DoesNotContain("LGTYP", whereText);
        Assert.DoesNotContain("LGPLA", whereText);
        Assert.DoesNotContain("CHARG", whereText);
    }

    [Fact]
    public void ParseBlockedStockRows_maps_columns_in_LquaColumns_order()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "1000|PDR|B01|30005R|12.500|BATCH1|S|K|VENDOR1" },
                }
            }
        };

        var rows = QualityHelpers.ParseBlockedStockRows(response);

        Assert.Single(rows);
        Assert.Equal("1000", rows[0].StorageLocation);
        Assert.Equal("PDR", rows[0].StorageType);
        Assert.Equal("B01", rows[0].Bin);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal(12.5m, rows[0].AvailableQty);
        Assert.Equal("BATCH1", rows[0].Batch);
        Assert.Equal("S", rows[0].StockCategory);
        Assert.Equal("K", rows[0].SpecialStockInd);
        Assert.Equal("VENDOR1", rows[0].SpecialStockNum);
    }

    [Fact]
    public void ParseBlockedStockRows_returns_empty_when_the_table_is_missing()
    {
        var response = new RfcResponse { Tables = new() };
        Assert.Empty(QualityHelpers.ParseBlockedStockRows(response));
    }

    private static QualityMb1bRequest MakeBody() => new()
    {
        Material = "30005R", Quantity = 10, Header = "Block", StorageLocation = "1000",
        BinType = "PDR", Bin = "B01", Username = "j.smith",
    };

    [Fact]
    public void PrepTransferOrderRequest_for_BLOCK_moves_stock_into_the_922_BLOCK_bin_first()
    {
        var (primary, secondary) = QualityHelpers.PrepTransferOrderRequest(MakeBody(), "BLOCK");

        Assert.Equal("922", primary.SourceType);
        Assert.Equal("BLOCK", primary.SourceBin);
        Assert.Equal("PDR", primary.DestinationType);
        Assert.Equal("B01", primary.DestinationBin);
        Assert.Equal("S", primary.StockCategory);

        Assert.Equal("PDR", secondary.SourceType);
        Assert.Equal("B01", secondary.SourceBin);
        Assert.Equal("922", secondary.DestinationType);
        Assert.Equal("BLOCK", secondary.DestinationBin);
        Assert.Equal("", secondary.StockCategory);
    }

    [Fact]
    public void PrepTransferOrderRequest_for_UNBLOCK_reverses_the_BLOCK_direction()
    {
        var (primary, secondary) = QualityHelpers.PrepTransferOrderRequest(MakeBody(), "UNBLOCK");

        Assert.Equal("PDR", primary.SourceType);
        Assert.Equal("B01", primary.SourceBin);
        Assert.Equal("922", primary.DestinationType);
        Assert.Equal("BLOCK", primary.DestinationBin);
        Assert.Equal("S", primary.StockCategory);

        Assert.Equal("922", secondary.SourceType);
        Assert.Equal("BLOCK", secondary.SourceBin);
        Assert.Equal("PDR", secondary.DestinationType);
        Assert.Equal("B01", secondary.DestinationBin);
    }

    [Fact]
    public void PrepTransferOrderRequest_throws_on_an_unrecognised_block_direction()
    {
        Assert.Throws<ArgumentException>(() => QualityHelpers.PrepTransferOrderRequest(MakeBody(), "SIDEWAYS"));
    }

    [Fact]
    public void BuildMb1bBlockedRequest_uses_movement_type_344_for_BLOCK_and_343_for_UNBLOCK()
    {
        var blockReq = QualityHelpers.BuildMb1bBlockedRequest(MakeBody(), "BLOCK");
        var unblockReq = QualityHelpers.BuildMb1bBlockedRequest(MakeBody(), "UNBLOCK");

        var blockRows = blockReq.InputTablesItems["BDCTABLE"];
        var unblockRows = unblockReq.InputTablesItems["BDCTABLE"];

        Assert.Contains(blockRows, r => r.GetValueOrDefault("FNAM") as string == "RM07M-BWARTWA" && (string?)r["FVAL"] == "344");
        Assert.Contains(unblockRows, r => r.GetValueOrDefault("FNAM") as string == "RM07M-BWARTWA" && (string?)r["FVAL"] == "343");
    }

    [Fact]
    public void ParseQualityResponse_reads_MESSG_from_each_of_the_three_underlying_calls()
    {
        RfcResponse mk(string msg) => new() { Parameters = new() { ["MESSG"] = msg } };

        var result = QualityHelpers.ParseQualityResponse(mk("MB1B posted"), mk("Moved to non-blocked"), mk("Moved to blocked"));

        Assert.Equal("MB1B posted", result.Mb1bMessage);
        Assert.Equal("Moved to non-blocked", result.ToNonBlockedMessage);
        Assert.Equal("Moved to blocked", result.ToBlockedMessage);
    }

    [Fact]
    public void ParseQualityResponse_succeeds_when_the_MB11_leg_reports_a_non_error_type_and_the_whm_legs_are_empty()
    {
        // Mirrors a non-WHM storage location: QualityController never
        // attempts a transfer order, so toNonBlocked/toBlocked are
        // genuinely empty RfcResponse()s.
        var mb1b = new RfcResponse { Parameters = new() { ["MESSG"] = "S    M7   001 MB1B posted" } };

        var result = QualityHelpers.ParseQualityResponse(mb1b, new RfcResponse(), new RfcResponse());

        Assert.True(result.Success);
    }

    // Regression test: previously ParseQualityResponse only kept the raw
    // MESSG text and never looked at the message type, so a rejected MB11
    // block/unblock was indistinguishable from a successful one to
    // QualityController — the stock movement never actually happened even
    // though the endpoint reported success.
    [Fact]
    public void ParseQualityResponse_fails_when_the_MB11_leg_reports_an_SAP_error()
    {
        var mb1b = new RfcResponse { Parameters = new() { ["MESSG"] = "E    M7   021 Deficit of SL stock" } };

        var result = QualityHelpers.ParseQualityResponse(mb1b, new RfcResponse(), new RfcResponse());

        Assert.False(result.Success);
    }

    [Fact]
    public void ParseQualityResponse_fails_when_either_WHM_transfer_order_legs_RETURN_table_has_a_blocking_error()
    {
        var mb1b = new RfcResponse { Parameters = new() { ["MESSG"] = "S    M7   001 MB1B posted" } };
        RfcResponse WithReturn(string type) => new()
        {
            Tables = new() { ["RETURN"] = new() { new() { ["TYPE"] = type, ["MESSAGE"] = "Bin does not exist" } } },
        };

        var toNonBlockedFails = QualityHelpers.ParseQualityResponse(mb1b, WithReturn("E"), WithReturn("S"));
        Assert.False(toNonBlockedFails.Success);

        var toBlockedFails = QualityHelpers.ParseQualityResponse(mb1b, WithReturn("S"), WithReturn("E"));
        Assert.False(toBlockedFails.Success);

        var bothSucceed = QualityHelpers.ParseQualityResponse(mb1b, WithReturn("S"), WithReturn("S"));
        Assert.True(bothSucceed.Success);
    }
}
