using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class CustomsHelpersTests
{
    [Fact]
    public void BuildLipsRequest_filters_to_this_plant_and_positive_quantity_lines()
    {
        var request = CustomsHelpers.BuildLipsRequest(new LipsRequest { Deliveries = ["80001234"] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LIPS~WERKS EQ '3012'", whereText);
        Assert.Contains("LIPS~KCMENG > 0", whereText);
        Assert.Equal("0080001234", request.InputTablesItems["value_list"][0]["LOW"]);
    }

    [Fact]
    public void ParseLipsRows_skips_the_header_row_and_maps_columns_in_order()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELN|POSNR|MATNR|KCMENG" },
                    new() { ["WA"] = "0080001234|000010|30005R|100" },
                }
            }
        };

        var rows = CustomsHelpers.ParseLipsRows(response);

        Assert.Single(rows);
        Assert.Equal("0080001234", rows[0].DeliveryNumber);
        Assert.Equal("30005R", rows[0].MaterialNumber);
        Assert.Equal("100", rows[0].Quantity);
    }

    [Fact]
    public void BuildVbfaRequest_filters_to_billing_document_flow_records_and_dedupes_deliveries()
    {
        var request = CustomsHelpers.BuildVbfaRequest(new VbfaRequest
        {
            Lines = [new VbfaLine("80001234", "000010"), new VbfaLine("80001234", "000020")], // same delivery twice
        });

        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("VBFA~VBTYP_N EQ 'M'", whereText);
        Assert.Single(request.InputTablesItems["value_list"]); // deduped to one delivery
    }

    [Fact]
    public void ParseVbfaRows_only_returns_rows_matching_a_requested_delivery_item_pair()
    {
        var req = new VbfaRequest { Lines = [new VbfaLine("80001234", "10")] };
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELV|POSNV|VBELN|POSNN|RFWRT" },
                    new() { ["WA"] = "0080001234|000010|4500099999|000010|150.00" }, // matches requested (delivery, item)
                    new() { ["WA"] = "0080001234|000020|4500099998|000020|75.00" },  // same delivery, different item — not requested
                }
            }
        };

        var rows = CustomsHelpers.ParseVbfaRows(response, req);

        Assert.Single(rows);
        Assert.Equal("4500099999", rows[0].InvoiceNumber);
    }

    [Fact]
    public void ParseKna1Rows_maps_the_customer_master_fields_used_to_auto_create_a_Destination()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "KUNNR|NAME1|STRAS|ORT01|PSTLZ|LAND1|LZONE" },
                    new() { ["WA"] = "0000012345|Acme Ltd|1 Main St|Normanton|WF6 1TN|GB|Z1" },
                }
            }
        };

        var rows = CustomsHelpers.ParseKna1Rows(response);

        Assert.Single(rows);
        Assert.Equal("Acme Ltd", rows[0].Name);
        Assert.Equal("GB", rows[0].DestinationCountry);
        Assert.Equal("Z1", rows[0].TransportZone);
    }

    [Fact]
    public void ParseKnvvIncoterms_keeps_only_the_first_Incoterms_seen_per_customer()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "KUNNR|VKORG|INCO1" },
                    new() { ["WA"] = "0000012345|3012|DAP" },
                    new() { ["WA"] = "0000012345|3012|EXW" }, // second sales-area row for the same customer — ignored
                }
            }
        };

        var dict = CustomsHelpers.ParseKnvvIncoterms(response);

        Assert.Equal("DAP", dict["0000012345"]);
    }

    [Fact]
    public void ParseKnvvIncoterms_skips_a_row_with_a_blank_customer_code()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "|3012|DAP" } } },
        };
        Assert.Empty(CustomsHelpers.ParseKnvvIncoterms(response));
    }
}
