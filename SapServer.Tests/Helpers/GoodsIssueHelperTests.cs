using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class GoodsIssueHelperTests
{
    [Fact]
    public void BuildGoodsIssueRequest_pads_the_delivery_number_to_10_on_both_HEADER_DATA_and_HEADER_CONTROL()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });
        Assert.Equal("0080001234", request.StructImportParameters["HEADER_DATA"]["DELIV_NUMB"]);
        Assert.Equal("0080001234", request.StructImportParameters["HEADER_CONTROL"]["DELIV_NUMB"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_sets_POST_GI_FLG_X_on_HEADER_CONTROL()
    {
        // Confirmed via a real BAPI Inspector signature (2026-08-28):
        // POST_GI_FLG lives on HEADER_CONTROL (BAPIOBDLVHDRCTRLCON), not
        // HEADER_DATA as the user's original sample code suggested — see
        // GoodsIssueModels.cs's header comment for the full diagnosis.
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });
        Assert.Equal("X", request.StructImportParameters["HEADER_CONTROL"]["POST_GI_FLG"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_sets_ITEM_DATA_SPL_QTY_POST_per_item()
    {
        // Regression test: confirmed live that POST_GI_FLG alone gets
        // rejected with "Delivery has not yet been put away / picked
        // (completely)" -- ITEM_DATA_SPL-QTY_POST per item is what actually
        // confirms picking. Item numbers here are SAP's own real
        // (auto-assigned, batch-split) sub-item numbers, not what was
        // originally requested when creating the split.
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest
        {
            DeliveryNumber = "0082291409",
            Items =
            [
                new GoodsIssueItem { ItemNumber = "900001", Quantity = 400m, BaseUom = "EA" },
                new GoodsIssueItem { ItemNumber = "900002", Quantity = 400m, BaseUom = "EA" },
                new GoodsIssueItem { ItemNumber = "900003", Quantity = 400m, BaseUom = "EA" },
            ],
        });

        var rows = request.InputTables["ITEM_DATA_SPL"];
        Assert.Equal(3, rows.Count);
        Assert.Equal("900001", rows[0]["DELIV_ITEM"]);
        Assert.Equal(400m, rows[0]["QTY_POST"]);
        Assert.Equal("EA", rows[0]["BASE_UOM"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_registers_RETURN_output_table()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });

        Assert.Contains("TYPE", request.OutputTables["RETURN"]);
        Assert.Contains("MESSAGE", request.OutputTables["RETURN"]);
    }

    [Fact]
    public void ParseGoodsIssueResponse_succeeds_when_RETURN_has_no_blocking_message()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "S", ["MESSAGE"] = "Delivery processed" }] },
        };
        var result = GoodsIssueHelper.ParseGoodsIssueResponse(response, "80001234");

        Assert.True(result.Success);
        Assert.Equal("80001234", result.DeliveryNumber);
        Assert.Single(result.Messages);
        Assert.Equal("Delivery processed", result.Messages[0].Message);
    }

    [Fact]
    public void ParseGoodsIssueResponse_fails_when_RETURN_has_a_type_E_message()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "E", ["MESSAGE"] = "Delivery not found" }] },
        };
        var result = GoodsIssueHelper.ParseGoodsIssueResponse(response, "80001234");
        Assert.False(result.Success);
    }

    [Fact]
    public void ParseGoodsIssueResponse_succeeds_with_no_RETURN_rows_at_all()
    {
        var result = GoodsIssueHelper.ParseGoodsIssueResponse(new RfcResponse(), "80001234");
        Assert.True(result.Success);
        Assert.Empty(result.Messages);
    }
}
