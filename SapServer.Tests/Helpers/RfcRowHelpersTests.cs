using SapServer.Helpers;

namespace SapServer.Tests.Helpers;

public class RfcRowHelpersTests
{
    [Fact]
    public void GetString_returns_empty_for_a_missing_key()
    {
        var row = new Dictionary<string, object?>();
        Assert.Equal("", row.GetString("MISSING"));
    }

    [Fact]
    public void GetString_returns_empty_for_a_null_value()
    {
        var row = new Dictionary<string, object?> { ["K"] = null };
        Assert.Equal("", row.GetString("K"));
    }

    [Fact]
    public void GetString_stringifies_a_present_value()
    {
        var row = new Dictionary<string, object?> { ["K"] = 42 };
        Assert.Equal("42", row.GetString("K"));
    }

    [Fact]
    public void GetDecimal_returns_zero_for_a_missing_or_null_or_blank_value()
    {
        var row = new Dictionary<string, object?> { ["NULLVAL"] = null, ["BLANK"] = "   " };
        Assert.Equal(0m, row.GetDecimal("MISSING"));
        Assert.Equal(0m, row.GetDecimal("NULLVAL"));
        Assert.Equal(0m, row.GetDecimal("BLANK"));
    }

    [Fact]
    public void GetDecimal_parses_European_grouped_format_correctly()
    {
        var row = new Dictionary<string, object?> { ["K"] = "1.234,56" };
        Assert.Equal(1234.56m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_MISPARSES_a_plain_invariant_decimal_string_by_100x_BUG()
    {
        // GetDecimal unconditionally strips every '.' before converting ',' to
        // '.', assuming SAP always sends European-grouped numbers
        // ("1.234,56"). A value that's already plain invariant-culture
        // decimal text with no thousands separator — "1234.56" — has its
        // decimal point stripped as if it were a grouping separator, so it
        // parses as 123456, not 1234.56. This is a genuine pre-existing bug
        // (same "document actual behavior, don't silently fix" approach as
        // SapPadTests), not asserted-as-intended: any caller feeding this a
        // plain-format number silently gets a value 100x too large.
        var row = new Dictionary<string, object?> { ["K"] = "1234.56" };
        Assert.Equal(123456m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_returns_zero_for_unparseable_text()
    {
        var row = new Dictionary<string, object?> { ["K"] = "not-a-number" };
        Assert.Equal(0m, row.GetDecimal("K"));
    }
}
