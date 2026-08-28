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
    public void BuildDeliveryChangeRequest_also_pads_the_delivery_number_to_10_on_HEADER_DATA()
    {
        // Regression test: HEADER_DATA-DELIV_NUMB was never being set at
        // all, causing a real, confirmed-live VL 302 "Delivery & does not
        // exist" rejection even though the delivery genuinely existed —
        // see this class's header comment for the full diagnosis timeline.
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest { DeliveryNumber = "80001234" });
        Assert.Equal("0080001234", request.StructImportParameters["HEADER_DATA"]["DELIV_NUMB"]);
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
    public void BuildDeliveryChangeRequest_sets_SALES_UNIT_and_DLV_QTY_IMUNIT_defaulting_SalesUnit_to_BaseUom()
    {
        // Regression test for a real, confirmed-live SAP rejection (VLBAPI
        // 004 "quantity consistency check") — DLV_QTY alone isn't enough;
        // SALES_UNIT and DLV_QTY_IMUNIT are also required. SalesUnit
        // defaults to BaseUom when not given explicitly, since sales unit
        // equals base unit for the vast majority of real materials (e.g.
        // CP1442, the delivery this bug was confirmed live against).
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "0082291409",
            Items = [new DeliveryChangeItem { ItemNumber = "10", Material = "CP1442", Quantity = 1200m, BaseUom = "EA" }],
        });

        var dataRow = request.InputTables["ITEM_DATA"][0];
        Assert.Equal(1200m, dataRow["DLV_QTY"]);
        Assert.Equal(1200m, dataRow["DLV_QTY_IMUNIT"]);
        Assert.Equal("EA", dataRow["SALES_UNIT"]);
        Assert.Equal("EA", dataRow["SALES_UNIT_ISO"]);
        Assert.Equal("EA", dataRow["BASE_UOM"]);
        Assert.Equal("EA", dataRow["BASE_UOM_ISO"]);
        // Regression test for VL 268 ("Conversion factors 0:0 are zero, not
        // defined mathematically") — confirmed live these must not be left
        // unset; default to 1:1, matching this delivery's real LIPS-UMVKZ/
        // UMVKN.
        Assert.Equal(1m, dataRow["FACT_UNIT_NOM"]);
        Assert.Equal(1m, dataRow["FACT_UNIT_DENOM"]);
        Assert.Equal(1.0, dataRow["CONV_FACT"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_uses_explicit_conversion_factors_when_given_instead_of_defaulting_to_1_1()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items = [new DeliveryChangeItem
            {
                ItemNumber = "10", Quantity = 5.5m, BaseUom = "KG", SalesUnit = "PC",
                FactUnitNom = 2m, FactUnitDenom = 3m,
            }],
        });

        var dataRow = request.InputTables["ITEM_DATA"][0];
        Assert.Equal(2m, dataRow["FACT_UNIT_NOM"]);
        Assert.Equal(3m, dataRow["FACT_UNIT_DENOM"]);
        Assert.Equal(2.0 / 3.0, (double)dataRow["CONV_FACT"]!, precision: 10);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_uses_explicit_ISO_codes_when_given_instead_of_defaulting()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items = [new DeliveryChangeItem
            {
                ItemNumber = "10", Quantity = 5.5m, BaseUom = "KG", SalesUnit = "KG",
                BaseUomIso = "KGM", SalesUnitIso = "KGM",
            }],
        });

        var dataRow = request.InputTables["ITEM_DATA"][0];
        Assert.Equal("KGM", dataRow["SALES_UNIT_ISO"]);
        Assert.Equal("KGM", dataRow["BASE_UOM_ISO"]);
    }

    [Fact]
    public void BuildDeliveryChangeRequest_uses_an_explicit_SalesUnit_when_given_instead_of_BaseUom()
    {
        var request = DeliveryChangeHelper.BuildDeliveryChangeRequest(new DeliveryChangeRequest
        {
            DeliveryNumber = "80001234",
            Items = [new DeliveryChangeItem { ItemNumber = "10", Quantity = 5.5m, BaseUom = "KG", SalesUnit = "PC" }],
        });

        var dataRow = request.InputTables["ITEM_DATA"][0];
        Assert.Equal("PC", dataRow["SALES_UNIT"]);
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
