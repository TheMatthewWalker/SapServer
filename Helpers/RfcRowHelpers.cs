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

        // SAP decimals often come as "1.234,56" or "1234.56"
        s = s.Replace(".", "").Replace(',', '.');

        return decimal.TryParse(
            s,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d)
            ? d
            : 0m;
    }
}
