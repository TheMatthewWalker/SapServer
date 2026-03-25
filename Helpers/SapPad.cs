namespace SapServer.Helpers;

public static class SapPad
{
    /// <summary>
    /// Pads <paramref name="value"/> to exactly <paramref name="length"/> characters.
    /// - Null/empty → returns empty string (no padding applied).
    /// - All-digit strings → left-padded with '0' (SAP ALPHA conversion).
    /// - Mixed/alpha strings → left-padded with ' ' (space).
    /// - Already >= length → returned unchanged (no truncation).
    /// </summary>
    public static string Pad(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length >= length)      return value;

        if (!value.All(char.IsDigit)) return value;
        return value.PadLeft(length, '0');
    }
}
