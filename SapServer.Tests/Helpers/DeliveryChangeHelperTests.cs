using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class DeliveryChangeHelperTests
{
    [Fact]
    public void BuildDeliveryChangeRequest_pads_the_delivery_number_to_10_on_HEADER_CONTROL()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest { DeliveryNumber = "80001234" });
        Assert.Equal("0080001234", request.StructImportParameters["HEADER_CONTROL"]["DELIV_NUMB"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_pads_each_item_number_to_6_and_sets_CHG_DELQTY_X()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items = [new DeliveryChangeItem { ItemNumber = "10", Quantity = 5.5m, BaseUom = "KG" }],
        });

        var controlRow = request.InputTables["ITEM_CONTROL"][0];
        Assert.Equal("0080001234", controlRow["DELIV_NUMB"]);
        Assert.Equal("000010", controlRow["DELIV_ITEM"]);
        Assert.Equal("X", controlRow["CHG_DELQTY"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_sets_ITEM_DATA_quantity_and_uom_per_item()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items = [new DeliveryChangeItem { ItemNumber = "10", Material = "30005R", Quantity = 5.5m, BaseUom = "KG" }],
        });

        var dataRow = request.InputTables["ITEM_DATA"][0];
        Assert.Equal("000010", dataRow["DELIV_ITEM"]);
        // SapPad.Pad only zero-pads all-digit strings — "30005R" is mixed
        // alphanumeric, so it comes back unchanged (see SapPad.cs's own
        // documented non-digit-branch behavior).
        Assert.Equal("30005R", dataRow["MATERIAL"]);
        Assert.Equal(5.5m, dataRow["DLV_QTY"]);
        Assert.Equal("KG", dataRow["BASE_UOM"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_builds_one_ITEM_DATA_and_ITEM_CONTROL_row_per_item()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items =
            [
                new DeliveryChangeItem { ItemNumber = "10", Quantity = 1m },
                new DeliveryChangeItem { ItemNumber = "20", Quantity = 2m },
            ],
        });

        Assert.Equal(2, request.InputTables["ITEM_CONTROL"].Count);
        Assert.Equal(2, request.InputTables["ITEM_DATA"].Count);
        Assert.Equal("000020", request.InputTables["ITEM_DATA"][1]["DELIV_ITEM"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_registers_RETURN_output_table()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest { DeliveryNumber = "80001234" });
        Assert.Contains("TYPE", request.OutputTables["RETURN"]);
        Assert.Contains("MESSAGE", request.OutputTables["RETURN"]);
    }

    [Fact]
    public void ParseDeliveryChangeResponse_succeeds_when_RETURN_has_no_blocking_message()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "S", ["MESSAGE"] = "Delivery changed" }] },
        };
        var result = DeliveryChangeHelper.ParseDeliveryChangeResponse(response, "80001234");

        Assert.True(result.Success);
        Assert.Equal("80001234", result.DeliveryNumber);
        Assert.Single(result.Messages);
    }

    [Fact]
    public void ParseDeliveryChangeResponse_fails_when_RETURN_has_a_type_E_message()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "E", ["MESSAGE"] = "Item not found" }] },
        };
        var result = DeliveryChangeHelper.ParseDeliveryChangeResponse(response, "80001234");
        Assert.False(result.Success);
    }

    [Fact]
    public void ParseDeliveryChangeResponse_succeeds_with_no_RETURN_rows_at_all()
    {
        var result = DeliveryChangeHelper.ParseDeliveryChangeResponse(new RfcResponse(), "80001234");
        Assert.True(result.Success);
        Assert.Empty(result.Messages);
    }
}
