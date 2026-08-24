using SapServer.Models;

namespace SapServer.Helpers;

/// <summary>
/// Minimal ZRFC_READ_TABLES request/parse pair for the SAP NCo spike
/// (NcoTestController) — a single-material MARA lookup, deliberately the
/// smallest possible read to exercise connect + execute + parse end to end.
/// Same request shape as the COM-path helpers (ProductionHelpers.
/// BuildKgToUnitRequest, QualityHelpers.BuildBlockedStockRequest, etc.) —
/// RfcRequestBuilder/RfcRequest/SapDelimitedParser are transport-agnostic, so
/// this works unchanged against either NcoRfcService or the COM pool.
/// </summary>
internal static class NcoReadTablesHelper
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";

    private static readonly string[] MaraColumns = ["MATNR", "MTART", "MEINS"];

    internal static RfcRequest BuildMaterialLookupRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT", "1")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" });

        foreach (var field in MaraColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = field });

        builder.WhereCondition($"MARA~MATNR EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");
        builder.ReadTable("data_display");

        return builder.Build();
    }

    internal static List<string[]> ParseMaterialLookupRows(RfcResponse response) =>
        SapDelimitedParser.ParseRows(
            response.Tables.GetValueOrDefault("data_display") ?? new List<Dictionary<string, object?>>(),
            '|');
}
