using SapServer.Models;

namespace SapServer.Helpers;

public static class ReturnTableHelper
{
    public sealed record SapMessage(string Type, string Message);

    /// <summary>
    /// Extracts all messages from a SAP RETURN/BAPIRETURN table in the response.
    /// Returns an empty list if the table is absent or empty.
    ///
    /// A real BAPIRET2 message is sometimes a variable-substitution message
    /// with no static text of its own — TYPE is populated but MESSAGE comes
    /// back blank, with the real content sitting in MESSAGE_V1-V4 instead
    /// (standard SAP message-class behavior). Confirmed for real against a
    /// live SAP call (DeliveryChangeHelper, BAPI_OUTB_DELIVERY_CHANGE) —
    /// without this fallback, a genuine SAP rejection came back completely
    /// silent: TYPE told you E/W happened, MESSAGE told you nothing at all.
    /// This only ever has an effect for a caller whose ReadTable(...) call
    /// actually requested the MESSAGE_V1-4 columns in the first place — for
    /// every existing caller that only asks for TYPE/MESSAGE, those keys
    /// simply aren't present in the row and this is a no-op, identical to
    /// the old behavior.
    /// </summary>
    public static List<SapMessage> ExtractMessages(RfcResponse response, string tableName = "RETURN")
    {
        if (!response.Tables.TryGetValue(tableName, out var rows))
            return [];

        return rows.Select(row =>
        {
            var type    = row.TryGetValue("TYPE",    out var t) ? t?.ToString() ?? "" : "";
            var message = row.TryGetValue("MESSAGE", out var m) ? m?.ToString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(message))
            {
                var variables = new[] { "MESSAGE_V1", "MESSAGE_V2", "MESSAGE_V3", "MESSAGE_V4" }
                    .Select(key => row.TryGetValue(key, out var v) ? v?.ToString()?.Trim() : null)
                    .Where(v => !string.IsNullOrEmpty(v));
                message = string.Join(" ", variables);
            }

            return new SapMessage(type, message);
        }).ToList();
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
