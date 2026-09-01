using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class ConsignmentHelpersTests
{
    [Fact]
    public void BuildVendorGrRequest_filters_to_this_plant_consignment_stock_and_the_given_movement_type_and_vendor()
    {
        var request = ConsignmentHelpers.BuildVendorGrRequest("12345", "101");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MSEG~WERKS EQ '3012'", whereText);
        Assert.Contains("MSEG~BWART EQ '101'", whereText);
        Assert.Contains("MSEG~SOBKZ EQ 'K'", whereText);
        Assert.Contains("MSEG~LIFNR EQ '0000012345'", whereText); // padded to 10
    }

    [Fact]
    public void BuildVendorGrRequest_adds_a_posting_date_floor_only_when_sinceDate_is_given()
    {
        var withSince = ConsignmentHelpers.BuildVendorGrRequest("12345", "101", "01.01.2026");
        var withSinceText = string.Join(" ", withSince.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("MKPF~BUDAT GE '01.01.2026'", withSinceText);

        var withoutSince = ConsignmentHelpers.BuildVendorGrRequest("12345", "101");
        var withoutSinceText = string.Join(" ", withoutSince.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.DoesNotContain("BUDAT", withoutSinceText);
    }

    [Fact]
    public void BuildVendorGrRequest_is_called_once_per_movement_type_not_a_combined_IN_list()
    {
        // Documented in the helper's own header comment: multi-value BWART
        // filtering (parenthesised OR, IN(...), and the IN-opt/value_list
        // mechanism) was tried and abandoned after it silently returned zero
        // rows — the caller (ConsignmentController) is expected to call this
        // twice (101, then 102) and merge in C#, so a single request must
        // only ever carry one BWART value.
        var request101 = ConsignmentHelpers.BuildVendorGrRequest("12345", "101");
        var request102 = ConsignmentHelpers.BuildVendorGrRequest("12345", "102");

        var where101 = string.Join(" ", request101.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        var where102 = string.Join(" ", request102.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MSEG~BWART EQ '101'", where101);
        Assert.DoesNotContain("102", where101);
        Assert.Contains("MSEG~BWART EQ '102'", where102);
        Assert.DoesNotContain("101", where102);
    }

    [Fact]
    public void ParseVendorGrRows_signs_the_quantity_negative_for_an_H_SHKZG_reversal()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MATNR|MBLNR|ZEILE|MENGE|MEINS|LIFNR|XBLNR_MKPF|SHKZG|SMBLN|SMBLP|BLDAT|BUDAT" },
                    new() { ["WA"] = "30005R|4900012345|0001|100|KG|0000012345|INV1|S| | |01.01.2026|02.01.2026" }, // normal GR
                    new() { ["WA"] = "30005R|4900012346|0001|100|KG|0000012345|INV1|H| | |03.01.2026|04.01.2026" }, // 102 reversal
                }
            }
        };

        var rows = ConsignmentHelpers.ParseVendorGrRows(response);

        Assert.Equal(2, rows.Length);
        Assert.Equal(100m, rows[0].Quantity);
        Assert.Equal(-100m, rows[1].Quantity);
    }

    [Fact]
    public void ParseVendorGrRows_normalises_the_material_number_by_stripping_leading_zeros_from_purely_numeric_values()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header|only" },
                    new() { ["WA"] = "00012345|4900012345|0001|100|KG|0000012345|INV1|S| | |01.01.2026|02.01.2026" },
                }
            }
        };

        var rows = ConsignmentHelpers.ParseVendorGrRows(response);
        Assert.Equal("12345", rows[0].Material);
    }

    [Fact]
    public void ParseVendorGrRows_reads_SMBLN_SMBLP_when_populated_and_blanks_when_not()
    {
        // Confirmed for real against Raaj Ratna's live SAP data (2026-08-27):
        // an MBST cancellation line carries SMBLN/SMBLP pointing back at the
        // document+item it reverses — an ordinary GR/reversal leaves both blank.
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header|only" },
                    new() { ["WA"] = "30008R|5005206624|0001|1090.210|KG|0000200604|RMIE1052|H|5005206623|0001|29.04.2026|07.05.2026" }, // cancels 5005206623/0001
                    new() { ["WA"] = "30005R|5005131208|0001|991.110|KG|0000200604|RMI/E/0626|S| | |30.08.2025|17.11.2025" }, // ordinary GR, nothing reversed
                }
            }
        };

        var rows = ConsignmentHelpers.ParseVendorGrRows(response);

        Assert.Equal("5005206623", rows[0].ReversalOfMaterialDocument);
        Assert.Equal("0001", rows[0].ReversalOfMaterialDocItem);

        Assert.Equal("", rows[1].ReversalOfMaterialDocument);
        Assert.Equal("", rows[1].ReversalOfMaterialDocItem);
    }

    [Fact]
    public void ParseVendorGrRows_returns_empty_when_the_table_is_missing()
    {
        Assert.Empty(ConsignmentHelpers.ParseVendorGrRows(new RfcResponse()));
    }
}
