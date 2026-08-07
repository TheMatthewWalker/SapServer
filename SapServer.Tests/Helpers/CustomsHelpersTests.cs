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
                    new() { ["WA"] = "VBELV|POSNV|VBELN|POSNN|RFWRT|ERDAT" },
                    new() { ["WA"] = "0080001234|000010|4500099999|000010|150.00|20260515" }, // matches requested (delivery, item)
                    new() { ["WA"] = "0080001234|000020|4500099998|000020|75.00|20260516" },  // same delivery, different item — not requested
                }
            }
        };

        var rows = CustomsHelpers.ParseVbfaRows(response, req);

        Assert.Single(rows);
        Assert.Equal("4500099999", rows[0].InvoiceNumber);
        Assert.Equal("20260515", rows[0].InvoiceDate);
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
                    new() { ["WA"] = "KUNNR|NAME1|STRAS|ORT01|PSTLZ|LAND1|LZONE|STCEG" },
                    new() { ["WA"] = "0000012345|Acme Ltd|1 Main St|Normanton|WF6 1TN|GB|Z1|GB123456789" },
                }
            }
        };

        var rows = CustomsHelpers.ParseKna1Rows(response);

        Assert.Single(rows);
        Assert.Equal("Acme Ltd", rows[0].Name);
        Assert.Equal("GB", rows[0].DestinationCountry);
        Assert.Equal("Z1", rows[0].TransportZone);
        Assert.Equal("GB123456789", rows[0].VatNumber);
    }

    [Fact]
    public void BuildVbrkRequest_filters_on_padded_invoice_numbers()
    {
        var request = CustomsHelpers.BuildVbrkRequest(new VbrkRequest { Invoices = ["4500099999"] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("VBRK~VBELN IN opt", whereText);
        Assert.Equal("4500099999", request.InputTablesItems["value_list"][0]["LOW"]);
    }

    [Fact]
    public void ParseVbrkRows_skips_header_and_maps_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELN|WAERK" },
                    new() { ["WA"] = "4500099999|EUR" },
                }
            }
        };

        var rows = CustomsHelpers.ParseVbrkRows(response);

        Assert.Single(rows);
        Assert.Equal("4500099999", rows[0].InvoiceNumber);
        Assert.Equal("EUR", rows[0].Currency);
    }

    [Fact]
    public void BuildConsignmentPriceRequest_ORs_exact_match_customer_material_pairs_and_filters_valid_conditions()
    {
        var request = CustomsHelpers.BuildConsignmentPriceRequest(new ConsignmentPriceRequest
        {
            Lines = [new ConsignmentPriceLine("363533", "CP1166")],
        });

        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("A005~KUNNR EQ '0000363533'", whereText);
        Assert.Contains("A005~MATNR EQ 'CP1166'", whereText);
        Assert.Contains("A005~DATBI GT '", whereText);
    }

    [Fact]
    public void BuildConsignmentPriceRequest_with_no_lines_still_filters_on_validity_date_only()
    {
        var request = CustomsHelpers.BuildConsignmentPriceRequest(new ConsignmentPriceRequest());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.DoesNotContain("KUNNR EQ", whereText);
        Assert.Contains("A005~DATBI GT '", whereText);
    }

    [Fact]
    public void ParseConsignmentPriceRows_skips_header_maps_columns_and_dedupes_to_first_record_per_pair()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "KUNNR|MATNR|KBETR|KONWA|KPEIN" },
                    new() { ["WA"] = "0000363533|CP1166|12.50|EUR|1" },
                    new() { ["WA"] = "0000363533|CP1166|99.00|EUR|1" }, // second condition record for the same pair — ignored
                }
            }
        };

        var rows = CustomsHelpers.ParseConsignmentPriceRows(response);

        Assert.Single(rows);
        Assert.Equal("12.50", rows[0].Rate);
        Assert.Equal("EUR", rows[0].Currency);
        Assert.Equal("1", rows[0].PricingUnit);
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
