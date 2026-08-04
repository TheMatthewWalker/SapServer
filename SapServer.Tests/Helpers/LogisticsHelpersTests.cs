using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class LogisticsHelpersTests
{
    [Fact]
    public void BuildPicksheetRequest_filters_to_this_sales_org_with_no_delivery_filter_when_none_given()
    {
        var request = LogisticsHelpers.BuildPicksheetRequest();
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LIKP~VKORG EQ '3012'", whereText);
        Assert.False(request.InputTablesItems.ContainsKey("value_list"));
    }

    [Fact]
    public void BuildPicksheetRequest_adds_one_value_list_row_per_open_picksheet()
    {
        var request = LogisticsHelpers.BuildPicksheetRequest([
            new PicksheetRow { DeliveryNumber = "80001234" },
            new PicksheetRow { DeliveryNumber = "80005678" },
        ]);

        var values = request.InputTablesItems["value_list"];
        Assert.Equal(2, values.Count);
        Assert.Equal("0080001234", values[0]["LOW"]);
        Assert.Equal("0080005678", values[1]["LOW"]);
    }

    [Fact]
    public void ParsePicksheetRows_maps_the_five_pipe_delimited_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELN|KUNNR|KODAT|LFDAT|INCO1" },
                    new() { ["WA"] = "0080001234|0000012345|20260301|20260305|DAP" },
                }
            }
        };

        var rows = LogisticsHelpers.ParsePicksheetRows(response);

        Assert.Single(rows);
        Assert.Equal("0080001234", rows[0].DeliveryNumber);
        Assert.Equal("0000012345", rows[0].CustomerNumber);
        Assert.Equal("DAP", rows[0].Incoterms);
    }

    [Fact]
    public void ParsePicksheetRows_returns_empty_when_the_table_is_missing()
    {
        Assert.Empty(LogisticsHelpers.ParsePicksheetRows(new RfcResponse()));
    }

    [Fact]
    public void BuildVBUKRequest_filters_to_open_delivery_documents_only()
    {
        var request = LogisticsHelpers.BuildVBUKRequest();
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("VBUK~WBSTK EQ 'A'", whereText);
        Assert.Contains("VBUK~VBTYP EQ 'J'", whereText);
    }

    [Fact]
    public void ParseVBUKRows_maps_just_the_delivery_number_column()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELN" },
                    new() { ["WA"] = "0080001234" },
                }
            }
        };

        var rows = LogisticsHelpers.ParseVBUKRows(response);

        Assert.Single(rows);
        Assert.Equal("0080001234", rows[0].DeliveryNumber);
        Assert.Equal("", rows[0].CustomerNumber); // only DeliveryNumber is populated from VBUK
    }
}
