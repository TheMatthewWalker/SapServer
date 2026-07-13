using SapServer.Models;
using SapServer.Models.Bapi;

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

// ── Stage batch (persist qty + transfer order to picksheet bin) ───────────────
//
// See PicksheetHelpers' "Staging" region below for the full explanation. This
// is what /api/warehouse/picksheet-stage-batch accepts/returns.

public sealed record StagePicksheetBatchRequest(string Material, string Batch, string DeliveryNumber);

public sealed record StagePicksheetBatchResponse(
    bool Success,
    string TransferOrderNumber,
    decimal QuantityMoved,
    string DestinationBin,
    string DestinationType,
    bool BinWasCreated,
    string SourceType,
    string SourceBin,
    string? Error,
    List<SapReturnMessage> Messages);

// ── Unstage batch (reverse a staging transfer order) ───────────────────────
//
// See PicksheetHelpers' "Unstaging" region below. Called when a staged
// package is deleted from a pallet, to move the batch back out of the
// picksheet bin and free it up for other deliveries again.

public sealed record PicksheetUnstageBatchRequest(
    string Material, string Batch, string StagedBin, string OriginalSourceType, string OriginalSourceBin);

public sealed record PicksheetUnstageBatchResponse(
    bool Success,
    string TransferOrderNumber,
    decimal QuantityMoved,
    bool NothingToReverse,
    string? Error,
    List<SapReturnMessage> Messages);

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

    // ── Staging (bin check/create + transfer order into the picksheet bin) ──────
    //
    // Ported from the uploaded wm_lt01.xltm Excel macro (staging_code module):
    //   - Destination storage type is hardcoded to "916" everywhere the macro
    //     stages a batch to a delivery's bin (create_LS01's LAGP-LGTYP, and
    //     f2_To_loc_changed's ".to_type = 916"). There is no SAP default for
    //     this — 916 IS the picksheet-staging storage type at this site.
    //   - The destination bin is the delivery/picksheet number, zero-padded to
    //     10 digits (macro: n2c(Range("vbeln"), 10)) — SapPad.Pad(x, 10) here.
    //   - Before staging, the macro checks get_lagp(VBELN, lgtyp) (LAGP WHERE
    //     LGNUM + LGPLA) and, if the bin doesn't exist yet, runs create_LS01
    //     (BDC on transaction LS01, screens SAPML01S 0100/0101) to create it —
    //     "sometimes the picksheet BIN will not have been created yet".
    //   - The quantity staged is the batch's full on-hand quantity (macro:
    //     res(res_row,9) = CDbl(x(7))/1000, i.e. GESME/total-on-hand, not just
    //     what's required for the line) — staging moves the whole batch.

    internal const string StagingStorageType = "916";
    private const string StagingBinSection   = "001"; // LAGP-LGBER, from create_LS01

    // Fresh single material+batch lookup, re-queried at the moment of staging
    // rather than trusting whatever the frontend cached from the earlier
    // picksheet-stock call (stock can move between when the picksheet was
    // opened and when the operator clicks "add"). Includes LGORT, which the
    // display query above doesn't expose, since L_TO_CREATE_SINGLE needs it
    // as I_LGORT.
    private static readonly string[] BatchSnapshotColumns =
        ["MATNR", "CHARG", "LGTYP", "LGPLA", "LGORT", "GESME"];

    internal sealed record BatchSnapshotRow(
        string Material, string Batch, string StorageType, string Bin, string StorageLocation, decimal TotalQty);

    internal static RfcRequest BuildBatchSnapshotRequest(string material, string batch)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" });

        foreach (var field in BatchSnapshotColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder
            .WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'")
            .WhereCondition($"LQUA~MATNR EQ '{SapPad.Pad(material, 18)}'")
            .WhereCondition($"LQUA~CHARG EQ '{SapPad.Pad(batch, 10)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static BatchSnapshotRow? ParseBatchSnapshot(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return null;

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= BatchSnapshotColumns.Length)
            .Select(cols => new BatchSnapshotRow(
                Material:        cols[0],
                Batch:           cols[1],
                StorageType:     cols[2],
                Bin:             cols[3],
                StorageLocation: cols[4],
                TotalQty:        ParseSapDecimal(cols[5])))
            .FirstOrDefault();
    }

    // Mirrors the macro's get_lagp(lgpla, lgtyp): WHERE LGNUM = warehouse AND
    // LGPLA = bin, reading back LGTYP. No row back → bin doesn't exist yet.
    internal static RfcRequest BuildBinCheckRequest(string bin)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LAGP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LAGP", FIELDNAME = "LGTYP" });

        builder
            .WhereCondition($"LAGP~LGNUM EQ '{Warehouse}'")
            .WhereCondition($"LAGP~LGPLA EQ '{bin}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static bool BinExists(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return false;

        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).Count > 0;
    }

    // Direct port of the macro's create_LS01: BDC on transaction LS01,
    // storage type hardcoded to StagingStorageType ("916"), storage section
    // ("LGBER") "001". Caller must pass an already zero-padded (10-digit) bin.
    internal static RfcRequest BuildCreateBinRequest(string bin) =>
        BdcBuilder.For("LS01")
            .Screen("SAPML01S", "0100")
                .Field("BDC_OKCODE", "/00")
                .Field("LAGP-LGNUM", Warehouse)
                .Field("LAGP-LGTYP", StagingStorageType)
                .Field("LAGP-LGPLA", bin)
            .Screen("SAPML01S", "0101")
                .Field("BDC_OKCODE", "=BU")
                .Field("LAGP-LGBER", StagingBinSection)
            .Build();

    // ── Unstaging (reverse a staging transfer order) ────────────────────────────
    //
    // Called when a staged package is deleted from a pallet. Re-queries the
    // batch fresh rather than trusting the quantity recorded at staging time
    // (some of it may have been picked/consumed since) — if it's still
    // sitting in the picksheet's 916 bin, moves whatever's actually there
    // now back to the originally recorded source type/bin. If it's not
    // there anymore (already picked, or moved by something else since),
    // there's nothing to reverse — that's reported back as success with
    // NothingToReverse=true rather than an error, so a stale/already-
    // resolved package can still be deleted from the app.

    internal static bool ShouldReverse(BatchSnapshotRow? snapshot, string stagedBin) =>
        snapshot is not null
        && snapshot.StorageType == StagingStorageType
        && snapshot.Bin == stagedBin;

    internal static CreateTransferOrderRequest BuildUnstageTransferOrderBody(
        BatchSnapshotRow snapshot, string originalSourceType, string originalSourceBin) => new()
    {
        StorageLocation = snapshot.StorageLocation,
        Material        = snapshot.Material,
        Quantity        = snapshot.TotalQty,
        SourceType      = snapshot.StorageType,      // currently 916 (where it's staged now)
        SourceBin       = snapshot.Bin,
        DestinationType = originalSourceType,        // back to where it came from
        DestinationBin  = originalSourceBin,
        Batch           = snapshot.Batch
    };

    // SAP decimals often come back "1.234,56" (German/European display format)
    // from ZRFC_READ_TABLES — same normalization as RfcRowExtensions.GetDecimal,
    // duplicated here since this parses a plain delimited string column rather
    // than a raw table row dictionary.
    private static decimal ParseSapDecimal(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        var normalized = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}
