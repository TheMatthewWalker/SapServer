using System;
using System.Globalization;


namespace SapServer.Helpers;

public static class RfcRowExtensions
{
    public static string GetString(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null)
            return string.Empty;

        return value.ToString() ?? string.Empty;
    }

    public static decimal GetDecimal(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null)
            return 0m;

        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return 0m;

        return ParseSapDecimal(s) ?? 0m;
    }

    /// <summary>
    /// Detects the real decimal separator per-value instead of assuming SAP
    /// always sends European-grouped text ("1.234,56"). That blanket
    /// assumption was a genuine bug, confirmed for real against a live SAP
    /// system: several fields actually come back as plain invariant text
    /// with no thousands grouping at all ("1.234" meaning 1.234, not 1234) -
    /// unconditionally stripping every '.' as a grouping separator silently
    /// inflated those values by a power of ten matching however many digits
    /// followed the point (three decimal places -> 1000x too large, exactly
    /// the symptom reported). Whichever separator appears LAST in the string
    /// is the real decimal point; an earlier one (if both are present) is
    /// thousands-grouping and gets stripped. A lone separator with no other
    /// one present is always treated as the decimal point - raw RFC/WA-dump
    /// text has no reason to apply GUI-style thousands grouping on its own,
    /// so this is a strictly better default than the old "period is always
    /// grouping" assumption for the quantity/currency fields this helper is
    /// actually used for.
    /// </summary>
    internal static decimal? ParseSapDecimal(string raw)
    {
        var s = raw.Trim();
        int lastComma  = s.LastIndexOf(',');
        int lastPeriod = s.LastIndexOf('.');

        string normalized;
        if (lastComma >= 0 && lastPeriod >= 0)
        {
            normalized = lastComma > lastPeriod
                ? s.Replace(".", "").Replace(',', '.') // "1.234,56" - comma is the real decimal point
                : s.Replace(",", "");                   // "1,234.56" - period is the real decimal point
        }
        else if (lastComma >= 0)
        {
            normalized = s.Replace(',', '.'); // "1234,56" - comma-only European decimal
        }
        else
        {
            normalized = s; // "1234.56" or "1234" - already invariant, or a plain integer
        }

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}
