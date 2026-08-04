using SapServer.Helpers;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class GoodsReceiptHelperTests
{
    private static GoodsReceiptRequest MinimalBody(int lineNumber = 1) => new()
    {
        PurchaseOrder = "4500012345",
        LineNumber = lineNumber,
        Reference = "INB-000014",
        TrackingNumber = "TRACK1",
        AddressCode = "Supplier note",
        ShipmentCompletionDate = "01.02.2026",
        PostingDate = "01.02.2026",
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
}
