using SapServer.Helpers;

namespace SapServer.Tests.Helpers;

public class SapPadTests
{
    [Fact]
    public void Null_or_empty_returns_empty_string()
    {
        Assert.Equal("", SapPad.Pad(null, 18));
        Assert.Equal("", SapPad.Pad("", 18));
    }

    [Fact]
    public void All_digit_strings_are_left_padded_with_zeros()
    {
        Assert.Equal("000000000012345678", SapPad.Pad("12345678", 18));
    }

    // NOTE: SapPad.Pad's XML doc says mixed/alpha strings are "left-padded
    // with ' ' (space)", but the implementation's non-digit branch just
    // `return value;` unchanged — there is no space-padding branch at all.
    // This test asserts the actual current behavior; the doc comment vs.
    // code mismatch is worth the team's attention separately (flagged in
    // the test-suite handoff), since every WHERE-clause padded material
    // number in Helpers/* relies on this function's real behavior.
    [Fact]
    public void Mixed_alpha_strings_are_returned_unchanged_not_space_padded()
    {
        var result = SapPad.Pad("30005R", 10);
        Assert.Equal("30005R", result);
    }

    [Fact]
    public void A_value_already_at_or_over_the_target_length_is_returned_unchanged_not_truncated()
    {
        Assert.Equal("123456789012345678901", SapPad.Pad("123456789012345678901", 18));
    }
}
