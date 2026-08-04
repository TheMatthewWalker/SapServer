using SapServer.Helpers;

namespace SapServer.Tests.Helpers;

public class SapDelimitedParserTests
{
    [Fact]
    public void Split_trims_each_segment()
    {
        var result = SapDelimitedParser.Split(" 30005R | 3012 |  10.5 ", '|');
        Assert.Equal(new[] { "30005R", "3012", "10.5" }, result);
    }

    [Fact]
    public void ParseRows_skips_the_header_row_when_asked()
    {
        var sapRows = new List<Dictionary<string, object?>>
        {
            new() { ["WA"] = "MATNR|WERKS" },
            new() { ["WA"] = "30005R|3012" },
        };

        var result = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true);

        Assert.Single(result);
        Assert.Equal(new[] { "30005R", "3012" }, result[0]);
    }

    [Fact]
    public void ParseRows_skips_blank_WA_rows()
    {
        var sapRows = new List<Dictionary<string, object?>>
        {
            new() { ["WA"] = "" },
            new() { ["WA"] = "30005R|3012" },
        };

        var result = SapDelimitedParser.ParseRows(sapRows, '|');

        Assert.Single(result);
    }
}
