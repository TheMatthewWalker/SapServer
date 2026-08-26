using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class GoodsIssueHelperTests
{
    [Fact]
    public void BuildGoodsIssueRequest_pads_the_delivery_number_to_10_on_DELIVERY_EXTEND_and_REQUEST()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });

        Assert.Equal("0080001234", request.StructImportParameters["DELIVERY_EXTEND"]["DELIVERY_NUMBER"]);
        Assert.Equal("0080001234", request.InputTables["REQUEST"][0]["DOCUMENT_NUMB"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_leaves_CHECK_MODE_blank_when_neither_flag_is_set()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });
        Assert.Equal("", request.StructImportParameters["TECHN_CONTROL"]["CHECK_MODE"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_CheckMode_sets_TECHN_CONTROL_CHECK_MODE_X()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234", CheckMode = true });
        Assert.Equal("X", request.StructImportParameters["TECHN_CONTROL"]["CHECK_MODE"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_TestRun_also_sets_TECHN_CONTROL_CHECK_MODE_X()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234", TestRun = true });
        Assert.Equal("X", request.StructImportParameters["TECHN_CONTROL"]["CHECK_MODE"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_defaults_dates_to_today_when_not_supplied()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });

        var row = request.InputTables["REQUEST"][0];
        Assert.Equal(today, row["DELIVERY_DATE"]);
        Assert.Equal(today, row["GOODS_ISSUE_DATE"]);
    }

    [Fact]
    public void BuildGoodsIssueRequest_registers_RETURN_and_CREATEDITEMS_output_tables()
    {
        var request = GoodsIssueHelper.BuildGoodsIssueRequest(new GoodsIssueRequest { DeliveryNumber = "80001234" });

        Assert.Contains("TYPE", request.OutputTables["RETURN"]);
        Assert.Contains("MESSAGE", request.OutputTables["RETURN"]);
        Assert.Contains("DOCUMENT_NUMB", request.OutputTables["CREATEDITEMS"]);
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

    [Fact]
    public void ParseGoodsIssueResponse_counts_CREATEDITEMS_rows()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["CREATEDITEMS"] = [
                    new() { ["DOCUMENT_NUMB"] = "0080001234", ["DOCUMENT_ITEM"] = "000010" },
                    new() { ["DOCUMENT_NUMB"] = "0080001234", ["DOCUMENT_ITEM"] = "000020" },
                ],
            },
        };
        var result = GoodsIssueHelper.ParseGoodsIssueResponse(response, "80001234");
        Assert.Equal(2, result.CreatedItemCount);
    }
}
