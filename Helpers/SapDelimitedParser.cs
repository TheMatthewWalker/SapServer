namespace SapServer.Helpers;

public static class SapDelimitedParser
{
    /// <summary>
    /// Splits a single WA string by <paramref name="delimiter"/> and trims each segment.
    /// </summary>
    public static string[] Split(string wa, char delimiter = '|')
        => wa.Split(delimiter).Select(s => s.Trim()).ToArray();

    /// <summary>
    /// Parses WA rows from an output table returned by ZRFC_READ_TABLES.
    /// Each dictionary in <paramref name="sapRows"/> must contain a "WA" key.
    /// Set <paramref name="skipHeader"/> to true if the first row is a column-name header.
    /// Returns a list of trimmed string arrays, one per data row.
    /// </summary>
    public static List<string[]> ParseRows(
        IEnumerable<Dictionary<string, object?>> sapRows,
        char delimiter,
        bool skipHeader = false)
    {
        var result = new List<string[]>();
        bool first = true;

        foreach (var row in sapRows)
        {
            if (first && skipHeader) { first = false; continue; }
            first = false;

            var wa = row.TryGetValue("WA", out var val) ? val?.ToString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(wa))
                result.Add(Split(wa, delimiter));
        }

        return result;
    }
}
