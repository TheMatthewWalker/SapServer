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
    public void GetDecimal_parses_a_plain_invariant_decimal_string_correctly()
    {
        // GetDecimal used to unconditionally strip every '.' before converting
        // ',' to '.', assuming SAP always sends European-grouped numbers
        // ("1.234,56"). Confirmed for real against a live SAP system that this
        // assumption is wrong: some fields come back as plain invariant text
        // with no thousands grouping at all, and stripping the '.' as if it
        // were a grouping separator inflated the value by a power of ten
        // (three decimal places -> 1000x too large). Fixed via ParseSapDecimal
        // detecting the real separator per-value instead of assuming a fixed
        // format — see its doc comment in RfcRowHelpers.cs.
        var row = new Dictionary<string, object?> { ["K"] = "1234.56" };
        Assert.Equal(1234.56m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_parses_a_lone_period_as_the_decimal_point()
    {
        var row = new Dictionary<string, object?> { ["K"] = "1234.5" };
        Assert.Equal(1234.5m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_parses_a_lone_comma_as_the_decimal_point()
    {
        var row = new Dictionary<string, object?> { ["K"] = "1234,5" };
        Assert.Equal(1234.5m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_parses_period_grouped_comma_decimal_as_the_last_separator_wins()
    {
        // "1,234.56" — period is the LAST separator, so it's the real decimal
        // point and the comma is thousands-grouping to be stripped.
        var row = new Dictionary<string, object?> { ["K"] = "1,234.56" };
        Assert.Equal(1234.56m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_parses_a_plain_integer_with_no_separator_at_all()
    {
        var row = new Dictionary<string, object?> { ["K"] = "1234" };
        Assert.Equal(1234m, row.GetDecimal("K"));
    }

    [Fact]
    public void GetDecimal_returns_zero_for_unparseable_text()
    {
        var row = new Dictionary<string, object?> { ["K"] = "not-a-number" };
        Assert.Equal(0m, row.GetDecimal("K"));
    }
}
