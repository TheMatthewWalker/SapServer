using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class NcoReadTablesHelperTests
{
    [Fact]
    public void BuildMaterialLookupRequest_uppercases_the_material_in_the_where_clause()
    {
        var request = NcoReadTablesHelper.BuildMaterialLookupRequest("30005r");

        Assert.Equal("ZRFC_READ_TABLES", request.FunctionName);
        Assert.Equal("MARA", request.InputTables["QUERY_TABLES"][0]["TABNAME"]);

        // "30005r" is mixed alnum, so SapPad.Pad leaves it unpadded (see its
        // documented non-digit-branch quirk) — only ToUpperInvariant applies.
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("MARA~MATNR EQ '30005R'", whereText);

        Assert.True(request.OutputTables.ContainsKey("data_display"));
        Assert.Empty(request.OutputTables["data_display"]); // no fields → WA column only
    }

    [Fact]
    public void BuildMaterialLookupRequest_zero_pads_an_all_digit_material()
    {
        var request = NcoReadTablesHelper.BuildMaterialLookupRequest("30005");

        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("MARA~MATNR EQ '000000000000030005'", whereText);
    }

    [Fact]
    public void BuildMaterialLookupRequest_requests_MATNR_MTART_MEINS_fields()
    {
        var request = NcoReadTablesHelper.BuildMaterialLookupRequest("30005R");

        var fields = request.InputTablesItems["query_FIELDS"]
            .Select(r => r["FIELDNAME"])
            .ToArray();

        Assert.Equal(["MATNR", "MTART", "MEINS"], fields);
    }

    [Fact]
    public void ParseMaterialLookupRows_splits_WA_rows_on_the_pipe_delimiter()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "30005R|FERT|EA" },
                }
            }
        };

        var rows = NcoReadTablesHelper.ParseMaterialLookupRows(response);

        Assert.Single(rows);
        Assert.Equal(["30005R", "FERT", "EA"], rows[0]);
    }

    [Fact]
    public void ParseMaterialLookupRows_returns_empty_when_data_display_is_missing()
    {
        var response = new RfcResponse();

        var rows = NcoReadTablesHelper.ParseMaterialLookupRows(response);

        Assert.Empty(rows);
    }
}
