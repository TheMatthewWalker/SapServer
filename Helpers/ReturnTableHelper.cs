using SapServer.Models;

namespace SapServer.Helpers;

public static class ReturnTableHelper
{
    public sealed record SapMessage(string Type, string Message);

    /// <summary>
    /// Extracts all messages from a SAP RETURN/BAPIRETURN table in the response.
    /// Returns an empty list if the table is absent or empty.
    /// </summary>
    public static List<SapMessage> ExtractMessages(RfcResponse response, string tableName = "RETURN")
    {
        if (!response.Tables.TryGetValue(tableName, out var rows))
            return [];

        return rows.Select(row => new SapMessage(
            Type:    row.TryGetValue("TYPE",    out var t) ? t?.ToString() ?? "" : "",
            Message: row.TryGetValue("MESSAGE", out var m) ? m?.ToString() ?? "" : ""
        )).ToList();
    }

    /// <summary>
    /// Returns true if any message has TYPE "E" (Error) or "A" (Abend) —
    /// meaning the RFC succeeded at transport level but failed at business level.
    /// </summary>
    public static bool HasBlockingError(IEnumerable<SapMessage> messages)
        => messages.Any(m => m.Type is "E" or "A");

    /// <summary>Reads a named scalar export parameter from the response as a string.</summary>
    public static string? GetParam(RfcResponse response, string paramName)
        => response.Parameters.TryGetValue(paramName, out var val) ? val?.ToString() : null;
}
