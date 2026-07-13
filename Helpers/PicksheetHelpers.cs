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

public sealed record PicksheetLipsRequest
{
    public List<string> Deliveries { get; init; } = [];
}

public sealed record PicksheetLipsRow(string DeliveryNumber, string ItemNumber, string MaterialNumber, string Quantity);

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

    // LFIMG (delivery quantity) not KCMENG (confirmed quantity) — the customs
    // feature's LIPS query (CustomsHelpers.BuildLipsRequest) filters on
    // KCMENG > 0, which is only populated once a delivery has actually been
    // picked/confirmed. A picksheet is precisely a delivery that HASN'T been
    // picked yet, so KCMENG is always 0 at this stage — that's why the
    // picksheet builder was coming back with zero material lines for every
    // delivery. LFIMG is the quantity documented on the delivery itself,
    // populated as soon as the delivery exists, regardless of pick status.
    // Also no WERKS (plant) restriction here, unlike the customs query —
    // a picksheet should show whatever the delivery actually needs.
    private static readonly string[] LipsColumns = ["VBELN", "POSNR", "MATNR", "LFIMG"];

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

    internal static RfcRequest BuildLipsRequest(PicksheetLipsRequest request)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LIPS" });

        foreach (var field in LipsColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LIPS", FIELDNAME = field });

        builder
            .WhereCondition("LIPS~LFIMG > 0")
            .WhereCondition("LIPS~VBELN IN opt");

        foreach (var delivery in request.Deliveries)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LIPS", FIELDNAME = "VBELN",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(delivery, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static PicksheetLipsRow[] ParseLipsRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= LipsColumns.Length)
            .Select(cols => new PicksheetLipsRow(cols[0], cols[1], cols[2], cols[3]))
            .ToArray();
    }
}
