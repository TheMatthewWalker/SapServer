using SapServer.Models;

namespace SapServer.Helpers;

// ── Request/response models ──────────────────────────────────────────────────

public sealed record PicksheetStockRequest
{
    public List<string> Materials { get; init; } = [];
}

public sealed record PicksheetBatchRow(
    string Material,
    string Batch,
    string StorageType,
    string Bin,
    string TotalQty,
    string AvailableQty,
    string StockCategory,
    string SpecialStockInd,
    string SpecialStockNum,
    string PackagingMaterial,
    string AllocatedDelivery);

// ── Helpers ───────────────────────────────────────────────────────────────────
//
// Backs the warehouse picksheet's "what stock is available for this material"
// panel — a direct port of the Excel staging-tab macro's get_lqua, joined with
// ZPRODBATCH (as PerformanceHelpers.BuildStockRequest already does elsewhere)
// so a batch's packaging instruction (PALL_MATNR) and, critically, whether it's
// already tagged against another delivery (VBELN) come back in the same call.
// Filtered to a specific material list (unlike PerformanceHelpers' unfiltered
// pull for the turns dashboard) since this is always scoped to one picksheet's
// required materials.
internal static class PicksheetHelpers
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string Warehouse    = "312";

    // Column order must exactly match query_FIELDS registration order below.
    private static readonly string[] LquaColumns =
        ["MATNR", "CHARG", "LGTYP", "LGPLA", "GESME", "VERME", "BESTQ", "SOBKZ", "SONUM"];

    internal static RfcRequest BuildStockRequest(PicksheetStockRequest request)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" })
            .TableRow("QUERY_TABLES", new { TABNAME = "ZPRODBATCH" });

        foreach (var field in LquaColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "ZPRODBATCH", FIELDNAME = "PALL_MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "ZPRODBATCH", FIELDNAME = "VBELN" });
        builder.TableItemRow("join_FIELDS", new { TAB_FROM = "LQUA", FLD_FROM = "CHARG", TAB_TO = "ZPRODBATCH", FLD_TO = "CHARG" });

        builder
            .WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'")
            .WhereCondition("LQUA~MATNR IN opt");

        foreach (var material in request.Materials)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LQUA",
                FIELDNAME = "MATNR",
                SIGN = "I",
                OPTION = "EQ",
                LOW = SapPad.Pad(material, 18),
                HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static PicksheetBatchRow[] ParseStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        var expectedColumns = LquaColumns.Length + 2; // + PALL_MATNR, VBELN

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= expectedColumns)
            .Select(cols => new PicksheetBatchRow(
                Material:          cols[0],
                Batch:             cols[1],
                StorageType:       cols[2],
                Bin:               cols[3],
                TotalQty:          cols[4],
                AvailableQty:      cols[5],
                StockCategory:     cols[6],
                SpecialStockInd:   cols[7],
                SpecialStockNum:   cols[8],
                PackagingMaterial: cols[9],
                AllocatedDelivery: cols[10]))
            .ToArray();
    }
}
