using SapServer.Helpers;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class GoodsReceiptHelperTests
{
    private static GoodsReceiptRequest MinimalBody(int lineNumber = 1, decimal? quantity = null) => new()
    {
        PurchaseOrder = "4500012345",
        LineNumber = lineNumber,
        Reference = "INB-000014",
        TrackingNumber = "TRACK1",
        AddressCode = "Supplier note",
        ShipmentCompletionDate = "01.02.2026",
        PostingDate = "01.02.2026",
        Quantity = quantity,
    };

    [Theory]
    [InlineData(1, "00010")]
    [InlineData(2, "00020")]
    [InlineData(5, "00050")]
    public void BuildGoodsReceiptRequest_converts_the_1_based_line_number_to_SAPs_x10_item_number(int lineNumber, string expectedEbelp)
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody(lineNumber));
        var rows = request.InputTablesItems["BDCTABLE"];
        var ebelpRow = rows.First(r => r.GetValueOrDefault("FNAM") as string == "RM07M-EBELP");
        Assert.Equal(expectedEbelp, ebelpRow["FVAL"]);
    }

    [Fact]
    public void BuildGoodsReceiptRequest_floors_a_zero_or_negative_line_number_to_the_first_item()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody(0));
        var rows = request.InputTablesItems["BDCTABLE"];
        var ebelpRow = rows.First(r => r.GetValueOrDefault("FNAM") as string == "RM07M-EBELP");
        Assert.Equal("00010", ebelpRow["FVAL"]);
    }

    [Fact]
    public void BuildGoodsReceiptRequest_uses_movement_type_101_and_this_plant()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody());
        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "RM07M-BWARTWE" && (string?)r["FVAL"] == "101");
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "RM07M-WERKS" && (string?)r["FVAL"] == "3012");
    }

    [Fact]
    public void BuildGoodsReceiptRequest_passes_through_a_date_already_in_dd_MM_yyyy_format()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(new GoodsReceiptRequest
        {
            PurchaseOrder = "4500012345", LineNumber = 1, Reference = "R", TrackingNumber = "T", AddressCode = "A",
            ShipmentCompletionDate = "15.03.2026", PostingDate = "15.03.2026",
        });
        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "MKPF-BLDAT" && (string?)r["FVAL"] == "15.03.2026");
    }

    [Fact]
    public void BuildGoodsReceiptRequest_converts_a_yyyyMMdd_date_to_dd_MM_yyyy()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(new GoodsReceiptRequest
        {
            PurchaseOrder = "4500012345", LineNumber = 1, Reference = "R", TrackingNumber = "T", AddressCode = "A",
            ShipmentCompletionDate = "20260315", PostingDate = "20260315",
        });
        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "MKPF-BLDAT" && (string?)r["FVAL"] == "15.03.2026");
    }

    [Fact]
    public void BuildGoodsReceiptRequest_defaults_a_blank_date_to_today()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(new GoodsReceiptRequest
        {
            PurchaseOrder = "4500012345", LineNumber = 1, Reference = "R", TrackingNumber = "T", AddressCode = "A",
            ShipmentCompletionDate = "", PostingDate = "",
        });
        var rows = request.InputTablesItems["BDCTABLE"];
        var expected = DateTime.Now.ToString("dd.MM.yyyy");
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "MKPF-BLDAT" && (string?)r["FVAL"] == expected);
    }

    [Fact]
    public void BuildGoodsReceiptRequest_omits_MSEG_ERFMG_when_no_quantity_is_supplied_so_XFULL_drives_the_full_PO_qty()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody());
        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.DoesNotContain(rows, r => r.GetValueOrDefault("FNAM") as string == "MSEG-ERFMG(01)");
        Assert.Contains(rows, r => r.GetValueOrDefault("FNAM") as string == "XFULL" && (string?)r["FVAL"] == "X");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildGoodsReceiptRequest_treats_a_zero_or_negative_quantity_as_not_supplied(decimal quantity)
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody(quantity: quantity));
        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.DoesNotContain(rows, r => r.GetValueOrDefault("FNAM") as string == "MSEG-ERFMG(01)");
    }

    [Fact]
    public void BuildGoodsReceiptRequest_writes_a_supplied_quantity_into_MSEG_ERFMG_between_the_cursor_and_select_okcode()
    {
        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(MinimalBody(quantity: 42.5m));
        var rows = request.InputTablesItems["BDCTABLE"];

        var qtyIndex    = rows.FindIndex(r => r.GetValueOrDefault("FNAM") as string == "MSEG-ERFMG(01)");
        var cursorIndex = rows.FindIndex(r => r.GetValueOrDefault("FNAM") as string == "BDC_CURSOR");
        var selectIndex = rows.FindLastIndex(r => r.GetValueOrDefault("FNAM") as string == "BDC_OKCODE" && (string?)r["FVAL"] == "=SELE");

        Assert.True(qtyIndex >= 0, "Expected an MSEG-ERFMG(01) row when a quantity is supplied.");
        Assert.Equal("42.5", rows[qtyIndex]["FVAL"]); // FVAL is CHAR132 - see BdcBuilder.Field(decimal)

        Assert.True(cursorIndex < qtyIndex && qtyIndex < selectIndex,
            "Expected MSEG-ERFMG(01) to be written after the cursor positions on it and before the row is selected.");
    }
}
