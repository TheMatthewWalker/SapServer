using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class WarehouseHelpers
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreateTo    = "L_TO_CREATE_SINGLE";
    // Permission code is unchanged (still "consignment"-scoped in
    // SapDepartmentPermissions) even though MB1B no longer runs through
    // Z_RFC_CALL_TRANSACTION — see BuildMb1bRequest.
    internal const string FnConsignment = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    // T158G: GM_CODE "04" = transaction MB1B, "Transfer posting" — the code
    // path BAPI_GOODSMVT_CREATE uses for movement 411 (own stock <-> vendor
    // consignment). A different GM_CODE branch than StockAdjustmentHelper's
    // confirmed-working "06" (MB1C, 711/712/717/718) or GoodsReceiptHelper's
    // confirmed-broken "01" (MB01, 101) — this one is UNTESTED against this
    // SAP system. See ConsignmentMb1bRequest.TestRun and
    // WarehouseController.ConsignmentMb1b's dryRun param: test this via
    // test.http (TESTRUN, then a real posting on a throwaway
    // material/vendor) before trusting it the way the 711 case was.
    internal const string GmCodeTransferPosting = "04";

    // Column order must exactly match query_FIELDS registration order below.
    // WDATU = date of last goods movement into this quant — the GR date.
    internal static readonly string[] LquaColumns =
        ["LGORT", "LGTYP", "LGPLA", "MATNR", "VERME", "CHARG", "BESTQ", "SOBKZ", "SONUM", "WDATU"];

    // ── Stock ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildStockRequest(StockQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  query.RowCount)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" });

        foreach (var field in LquaColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'");

        if (!string.IsNullOrWhiteSpace(query.Material))
        {
            // Wildcard search is opt-in: a caller has to type '%' or '_'
            // themselves (native SQL wildcards, not ABAP's CP/'*' — this Z-RFC
            // runs a LIKE, not an Open SQL CP comparison) to get a pattern
            // match, e.g. "TSHV%" (starts with), "%TSHV" (ends with),
            // "%TSHV%" (contains), "TSH_V" (single-char wildcard). A plain
            // material number with neither character keeps going through the
            // padded exact-match EQ below, unchanged from before.
            if (query.Material.Contains('%') || query.Material.Contains('_'))
                builder.WhereCondition($"LQUA~MATNR LIKE '{query.Material.ToUpperInvariant()}'");
            else
                builder.WhereCondition($"LQUA~MATNR EQ '{SapPad.Pad(query.Material, 18)}'");
        }

        if (!string.IsNullOrWhiteSpace(query.StorageType))
            builder.WhereCondition($"LQUA~LGTYP EQ '{query.StorageType}'");

        if (!string.IsNullOrWhiteSpace(query.ExcludeStorageType))
            builder.WhereCondition($"LQUA~LGTYP NE '{query.ExcludeStorageType}'");

        if (!string.IsNullOrWhiteSpace(query.Bin))
            builder.WhereCondition($"LQUA~LGPLA EQ '{query.Bin}'");

        if (!string.IsNullOrWhiteSpace(query.Batch))
            builder.WhereCondition($"LQUA~CHARG EQ '{query.Batch}'");

        if (!string.IsNullOrWhiteSpace(query.StorageLocation))
            builder.WhereCondition($"LQUA~LGORT EQ '{query.StorageLocation}'");

        if (!string.IsNullOrWhiteSpace(query.StockCategory))
            builder.WhereCondition($"LQUA~BESTQ EQ '{query.StockCategory}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    // profitCentres is an optional Material→PRCTR lookup (PerformanceHelpers.
    // BuildMaterialProfitCentre/ParseMaterialProfitCentre — PRCTR lives on MARC,
    // not LQUA, so it's a separate RFC call the controller joins in here rather
    // than something this function can look up on its own).
    internal static StockRow[] ParseStockRows(RfcResponse response, Dictionary<string, string>? profitCentres = null)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= LquaColumns.Length)
            .Select(cols => new StockRow
            {
                StorageLocation = cols[0],
                StorageType     = cols[1],
                Bin             = cols[2],
                Material        = cols[3],
                AvailableQty    = RfcRowExtensions.ParseSapDecimal(cols[4]) ?? 0m,
                Batch           = cols[5],
                StockCategory   = cols[6],
                SpecialStockInd = cols[7],
                SpecialStockNum = cols[8],
                GrDate          = cols[9],
                ProfitCentre    = profitCentres?.GetValueOrDefault(PerformanceHelpers.NormaliseMaterial(cols[3]), "") ?? ""
            })
            .ToArray();
    }

    internal static MaterialTotalRow[] AggregateByMaterial(StockRow[] rows) =>
        rows
            .GroupBy(r => r.Material)
            .Select(g => new MaterialTotalRow
            {
                Material   = g.Key,
                TotalQty   = g.Sum(r => r.AvailableQty),
                QuantCount = g.Count()
            })
            .OrderBy(r => r.Material)
            .ToArray();

    internal static BinSummaryRow[] AggregateByBin(StockRow[] rows) =>
        rows
            .GroupBy(r => (r.StorageType, r.Bin))
            .Select(g => new BinSummaryRow
            {
                StorageType = g.Key.StorageType,
                Bin         = g.Key.Bin,
                QuantCount  = g.Count(),
                TotalQty    = g.Sum(r => r.AvailableQty)
            })
            .OrderBy(r => r.StorageType).ThenBy(r => r.Bin)
            .ToArray();

    // ── IM stock (MARD) — Production Count, storage location 1716 ───────────────
    //
    // Confirmed against the real SAP system: 1716 has no WM/bin concept and
    // never appears in LQUA — MARD~LABST (unrestricted-use stock) is the
    // real source. Same ZRFC_READ_TABLES/data_display/skipHeader pattern as
    // BuildStockRequest/ParseStockRows above, just a different table.
    internal static readonly string[] MardColumns = ["WERKS", "LGORT", "MATNR", "LABST"];

    internal static RfcRequest BuildImStockRequest(ImStockQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  query.RowCount)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARD" });

        foreach (var field in MardColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = field });

        builder.WhereCondition($"MARD~WERKS EQ '{Plant}'");
        builder.WhereCondition($"MARD~LGORT EQ '{query.StorageLocation}'");

        if (!string.IsNullOrWhiteSpace(query.Material))
            builder.WhereCondition($"MARD~MATNR EQ '{SapPad.Pad(query.Material, 18)}'");

        builder.ReadTable("data_display");

        return builder.Build();
    }

    internal static ImStockRow[] ParseImStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= MardColumns.Length)
            .Select(cols => new ImStockRow
            {
                Plant           = cols[0],
                StorageLocation = cols[1],
                Material        = cols[2],
                AvailableQty    = RfcRowExtensions.ParseSapDecimal(cols[3]) ?? 0m,
            })
            .ToArray();
    }

    // ── Transfer Order ────────────────────────────────────────────────────────

    internal static RfcRequest BuildTransferOrderRequest(CreateTransferOrderRequest body) =>
        new RfcRequestBuilder(FnCreateTo)
            .Import("I_LGNUM", Warehouse)
            .Import("I_WERKS", Plant)
            .Import("I_LGORT", body.StorageLocation)
            .Import("I_SQUIT", "X")
            .Import("I_BWLVS", "999")
            .Import("I_MATNR", SapPad.Pad(body.Material, 18))
            .Import("I_ANFME", body.Quantity)
            .Import("I_CHARG", SapPad.Pad(body.Batch, 10))
            .Import("I_ZEUGN", SapPad.Pad(body.Batch, 10))
            .Import("I_VLTYP", body.SourceType)
            .Import("I_VLPLA", SapPad.Pad(body.SourceBin, 10))
            .Import("I_BESTQ", body.StockCategory ?? "")
            .Import("I_SOBKZ", body.SpecialStockIndicator ?? "")
            .Import("I_SONUM", SapPad.Pad(body.SpecialStockNumber, 16))
            .Import("I_NLPLA", SapPad.Pad(body.DestinationBin, 10))
            .Import("I_NLTYP", body.DestinationType)
            .ReadParam("E_TANUM")
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .Build();

    internal static CreateTransferOrderResponse ParseTransferOrderResponse(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");
        return new CreateTransferOrderResponse
        {
            TransferOrderNumber = ReturnTableHelper.GetParam(response, "E_TANUM") ?? "",
            Success             = true,
            Messages            = messages
                .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
                .ToList()
        };
    }

    // ── Destination bin existence check ──────────────────────────────────────
    //
    // L_TO_CREATE_SINGLE doesn't behave like a clean BAPI when the destination
    // bin (I_NLTYP/I_NLPLA) doesn't exist — instead of returning a business
    // error in RETURN, the underlying transaction the RFC drives hits a screen
    // it doesn't expect, the whole call fails at the RFC level (func.Call
    // returns false) with no SAP.Exception code and nothing in RETURN, and the
    // OCX connection is torn down and needs to reconnect. The end result:
    // a warehouse operator who typos a bin gets a raw "RFC call ... failed (no
    // detail available)" and has to retry once the session reconnects, with no
    // indication of what actually went wrong (see WarehouseController.
    // CreateTransferOrder for the fix — check the bin exists first and fail
    // fast with a clear message, exactly the same LAGP lookup pattern already
    // used by PicksheetHelpers.BuildBinCheckRequest/BinExists for staging
    // bins, just scoped to a specific storage type instead of hardcoded 916).
    internal static RfcRequest BuildBinCheckRequest(string storageType, string bin)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LAGP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LAGP", FIELDNAME = "LGPLA" });

        builder
            .WhereCondition($"LAGP~LGNUM EQ '{Warehouse}'")
            .WhereCondition($"LAGP~LGTYP EQ '{storageType}'")
            .WhereCondition($"LAGP~LGPLA EQ '{bin}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static bool BinExists(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return false;

        // skipHeader: true — data_display's first row is SAP's own column
        // header, not a real hit; PicksheetHelpers.BinExists established this
        // same fix already (see task history — "Filter SAP header row from
        // stock lookup") after a bin-existence check like this one first
        // shipped without it and reported every bin as existing.
        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).Count > 0;
    }

    // ── Bin → storage type lookup ────────────────────────────────────────────
    //
    // Same LAGP table as BuildBinCheckRequest, but the other direction: given
    // just a bin (no storage type filter), return every storage type LAGP has
    // that bin registered under. Backs the shared "auto-derive storage type
    // from a scanned/typed bin" QoL feature wired into the LT04 scan flow,
    // the LT04 modal, both Stock Management transfer forms, and the
    // standalone Transfer Orders tile. Expects paddedBin already padded by
    // the caller (SapPad.Pad(bin, 10)) — same convention CreateTransferOrder
    // already uses before calling BuildBinCheckRequest, not padding inside
    // the helper itself.
    internal static RfcRequest BuildBinStorageTypeLookupRequest(string paddedBin)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LAGP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LAGP", FIELDNAME = "LGTYP" });

        builder
            .WhereCondition($"LAGP~LGNUM EQ '{Warehouse}'")
            .WhereCondition($"LAGP~LGPLA EQ '{paddedBin}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static string[] ParseBinStorageTypeRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        // skipHeader: true — same ZRFC_READ_TABLES column-header gotcha as
        // BinExists/every other data_display parse in this file.
        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length > 0 && !string.IsNullOrWhiteSpace(cols[0]))
            .Select(cols => cols[0])
            .Distinct()
            .OrderBy(t => t)
            .ToArray();
    }

    // ── Delete TR (LB02) ─────────────────────────────────────────────────────
    //
    // Replicates wm_open_tr.xlsm's ati_code.delete_tr sub (recovered via
    // MS-OVBA decompression of the workbook's vbaProject.bin — the warehouse
    // team's Excel macro this whole TR-management feature is modernising).
    // Hard-deletes item "" (all items) via =DLK + the SAPLSPO1 confirm popup.
    //
    // SAP sometimes refuses with "E L2 019  You are not allowed to delete
    // transfer requirement item 0001" when the TR has a second line already
    // partially processed. The macro's own fallback for this (re-target
    // TBPOS "2" on screen SAPML02B/0102 and set a field it calls
    // "LTBP1-LVORM") is NOT ported here — confirmed against this real SAP
    // system that LTBP1-LVORM does not exist on dynpro SAPML02B/0102 at all
    // ("Field LTBP1-LVORM does not exist in dynpro SAPML02B 0102"), and the
    // decompiled macro source at that exact point is corrupted enough that
    // the true intended field/screen can't be reconstructed with confidence.
    // Since the macro wrapped this whole path in "On Error Resume Next", the
    // realistic explanation is this fallback branch was already silently
    // broken/dead in the original macro and nobody ever noticed. Rather than
    // guess at a second unverified BDC screen mapping, WarehouseController.
    // DeleteTr surfaces the "E L2 019" refusal directly (see
    // IsDeleteTrItemBlocked) so the operator knows this TR needs a manual
    // LB02 delete — a confirmed, correctly-recorded fallback can be added
    // once someone with SAP GUI access captures the real screen sequence
    // (e.g. via SHDB).
    internal static RfcRequest BuildDeleteTrRequest(string trNumber) =>
        BdcBuilder.For("LB02")
            .Screen("SAPML02B", "0100")
                .Field("BDC_OKCODE", "/00")
                .Field("LTBK-LGNUM", Warehouse)
                .Field("LTBK-TBNUM", trNumber)
                .Field("LTBP-TBPOS", "")
            .Screen("SAPML02B", "1103")
                .Field("BDC_OKCODE", "=DLK")
            .Screen("SAPLSPO1", "0400")
                .Field("BDC_OKCODE", "=YES")
            .Build();

    // True only for the exact "E L2 019" refusal — checked on the parsed
    // Type/MessageClass/MessageNumber triple (ProductionHelpers.
    // ParseBdcResponse already regex-splits MESSG into these), same idiom
    // PerformanceController already uses for its own S/M3/801 check, so
    // whitespace differences in SAP's own message text can't break this.
    internal static bool IsDeleteTrItemBlocked(BdcResponse result) =>
        result.Type == "E" && result.MessageClass == "L2" && result.MessageNumber == "019";

    // ── Delete TR verification ───────────────────────────────────────────────
    //
    // A BDC "success" can't be trusted at face value: this exact delete flow
    // was observed reporting Type "S" for "Field LTBP1-LVORM does not exist
    // in dynpro SAPML02B 0102" (SAP message class "00" — a BDC-processor/
    // framework message, not a business-transaction result) even though
    // nothing was actually deleted. ParseBdcResponse's regex has no way to
    // tell a real business success apart from a framework message that
    // happens to carry type 'S'. Rather than special-case message class "00"
    // (fragile — there's no guarantee every framework failure uses it, or
    // that every class "00" message is bad), WarehouseController.DeleteTr
    // re-queries LTBP after every delete attempt and only reports success if
    // the TR is actually gone — the same "verify the concrete outcome, don't
    // trust the raw message" approach CreateStockAdjustment already uses
    // (checking for a real MATERIALDOCUMENT rather than trusting SAP's
    // message alone).
    internal static RfcRequest BuildTrExistsRequest(string trNumber)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LTBP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "TBNUM" });

        builder
            .WhereCondition($"LTBP~LGNUM EQ '{Warehouse}'")
            .WhereCondition($"LTBP~TBNUM EQ '{trNumber}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static bool TrStillExists(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return false;

        // skipHeader: true — same ZRFC_READ_TABLES column-header gotcha as
        // BinExists/every other data_display parse in this file.
        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).Count > 0;
    }

    // ── TR Cleanup Candidates (LTBK/LTBP/MARC/MCHB → LQUA) ──────────────────
    //
    // Mirrors wm_open_tr.xltm's Get_LAGP_LQUA/Get_LQUA subs: extends the same
    // open-TR join with MCHB~CLABS (call 1: BuildTrCleanupCandidatesBaseRequest),
    // then re-queries LQUA filtered to just the batches found (call 2:
    // BuildTrCleanupLquaByBatchRequest) to check whether each batch currently
    // sits somewhere other than storage type 901.
    //
    // NOTE: the MCHB join below is effectively an inner join — a TR line
    // whose batch has no MCHB row at all (never posted any goods movement)
    // will be silently dropped from call 1's results rather than showing up
    // with a blank CLABS. Confirm against a real SAP system whether this
    // matches the macro's own behavior; if not, split into two separate LTBP
    // and MCHB lookups joined in C# instead of at the RFC layer.
    internal static readonly string[] TrCleanupBaseColumns =
        ["DISPO", "TBNUM", "MATNR", "LGORT", "MENGE", "MEINS", "CHARG", "CLABS"];

    internal static RfcRequest BuildTrCleanupCandidatesBaseRequest()
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LTBK" })
            .TableRow("QUERY_TABLES", new { TABNAME = "LTBP" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MCHB" });

        builder
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "DISPO" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "TBNUM" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MATNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "LGORT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MENGE" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MEINS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "CHARG" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MCHB", FIELDNAME = "CLABS" });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "MANDT", TAB_TO = "LTBP", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "LGNUM", TAB_TO = "LTBP", FLD_TO = "LGNUM" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "TBNUM", TAB_TO = "LTBP", FLD_TO = "TBNUM" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MANDT", TAB_TO = "MARC", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MATNR", TAB_TO = "MARC", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "WERKS", TAB_TO = "MARC", FLD_TO = "WERKS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MANDT", TAB_TO = "MCHB", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "WERKS", TAB_TO = "MCHB", FLD_TO = "WERKS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MATNR", TAB_TO = "MCHB", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "CHARG", TAB_TO = "MCHB", FLD_TO = "CHARG" });

        builder.WhereCondition($"LTBP~LGNUM EQ '{Warehouse}'");
        builder.WhereCondition("LTBK~STATU NE 'E'");
        builder.WhereCondition("LTBP~BESTQ EQ ''");

        return builder.ReadTable("data_display").Build();
    }

    // Intermediate row — not exposed outside this file; keeps the raw
    // material/batch/etc. needed to build the batch value_list below and the
    // final reason-flag logic, same "internal" scoping as
    // OpenTransferRequirementRow but not a public API model.
    internal sealed class TrCleanupBaseRow
    {
        internal string TrNumber        = "";
        internal string Material        = "";
        internal string StorageLocation = "";
        internal decimal Quantity;
        internal string Uom             = "";
        internal string MrpController   = "";
        internal string Batch           = "";
        internal decimal? UnrestrictedQty; // MCHB-CLABS, null/blank == "no stock"
    }

    internal static TrCleanupBaseRow[] ParseTrCleanupBaseRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= TrCleanupBaseColumns.Length)
            .Select(cols => new TrCleanupBaseRow
            {
                MrpController    = cols[0],
                TrNumber         = cols[1],
                Material         = cols[2],
                StorageLocation  = cols[3],
                Quantity         = RfcRowExtensions.ParseSapDecimal(cols[4]) ?? 0m,
                Uom              = cols[5],
                Batch            = cols[6],
                UnrestrictedQty  = RfcRowExtensions.ParseSapDecimal(cols[7]),
            })
            .ToArray();
    }

    // Uses the codebase's established "TABLE~FIELD IN opt" + value_list
    // range-table mechanism (ZRFC_READ_TABLES doesn't support a literal SQL
    // "IN (...)" where-clause) — same pattern as e.g.
    // CustomsHelpers.BuildLikpRequest/BuildLipsRequest,
    // PerformanceHelpers.AddInFilter.
    internal static RfcRequest BuildTrCleanupLquaByBatchRequest(IEnumerable<string> batches)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = "CHARG" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = "LGTYP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = "LGPLA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = "GESME" });

        builder.WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'");
        builder.WhereCondition("LQUA~CHARG IN opt");

        foreach (var batch in batches.Distinct())
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LQUA", FIELDNAME = "CHARG",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(batch, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    // Batch (unpadded, matching TrCleanupBaseRow.Batch/OpenTransferRequirementRow.Batch)
    // → true if any LQUA row currently has it in a non-901 bin with qty > 0,
    // i.e. it's already been transferred out and this TR is stale.
    internal static HashSet<string> ParseAlreadyTransferredBatches(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 4)
            .Where(cols => cols[1] != "901" && (RfcRowExtensions.ParseSapDecimal(cols[3]) ?? 0m) > 0)
            .Select(cols => cols[0].TrimStart('0'))
            .ToHashSet();
    }

    internal const string ReasonSloc1710           = "sloc_1710";
    internal const string ReasonNoStock            = "no_stock";
    internal const string ReasonAlreadyTransferred = "already_transferred";

    internal static TrCleanupCandidateRow[] BuildTrCleanupCandidateRows(
        TrCleanupBaseRow[] baseRows, RfcResponse? lquaResponse)
    {
        var transferred = lquaResponse is null
            ? []
            : ParseAlreadyTransferredBatches(lquaResponse);

        return baseRows
            .Select(r =>
            {
                var reasons = new List<string>();
                if (r.StorageLocation == "1710") reasons.Add(ReasonSloc1710);
                if (r.UnrestrictedQty is null or 0m) reasons.Add(ReasonNoStock);
                if (!string.IsNullOrWhiteSpace(r.Batch) && transferred.Contains(r.Batch.TrimStart('0')))
                    reasons.Add(ReasonAlreadyTransferred);

                return new TrCleanupCandidateRow
                {
                    TrNumber        = r.TrNumber,
                    Material        = r.Material,
                    Batch           = r.Batch,
                    StorageLocation = r.StorageLocation,
                    Quantity        = r.Quantity,
                    Uom             = r.Uom,
                    MrpController   = r.MrpController,
                    Reasons         = reasons.ToArray(),
                };
            })
            .Where(c => c.Reasons.Length > 0) // only flagged TRs are "candidates"
            .ToArray();
    }

    // ── Open Transfer Requirements (LTBK/LTBP) ──────────────────────────────────
    //
    // Mirrors wm_open_tr.xltm's Get_LAGP_LQUA sub (its name is a leftover
    // from an earlier version of that macro — it has always queried
    // LTBK/LTBP/MARC/MKPF, not LAGP/LQUA): same 4-table join, same field
    // list, and the macro's own LTBK~STATU <> 'E' / LTBP~BESTQ = '' WHERE
    // conditions, plus one extra condition (LTBK~TRART <> 'E') added on top
    // of the macro's own filter — see below.
    //
    // LTBK~STATU NE 'E' excludes only error/cancelled TR headers — there's
    // no explicit "not yet converted to a TO" flag in this select, because
    // this SAP system evidently doesn't need one for TRs generated from a
    // 131 movement (once fully converted, the LTBP row simply stops
    // appearing here, same assumption the macro has always relied on).
    // LTBK~TRART NE 'E' additionally excludes TR headers relating to a
    // goods receipt (not present in the macro; added deliberately here).
    // NOTE: this field was briefly swapped out for LTBP~ELIKZ <> 'X' (an
    // accidental side effect of an unrelated commit) which silently
    // filtered out TRs the macro correctly showed — if the Nexus tile ever
    // again shows fewer/no rows compared to the macro, check this WHERE
    // clause against get_tr.bas's Get_LAGP_LQUA sub first.
    // LTBP~BESTQ EQ '' excludes quality-blocked items (BESTQ 'Q') — the same
    // gate CheckQualityBlock enforces again per-item immediately before
    // posting, so a blocked line never shows up as pickable in the first
    // place, matching the macro's own two-layer check.
    // CHARG appended at the end rather than interleaved with the other
    // LTBP/LTBK fields, so the existing positional indices above (cols[0]
    // .. cols[13]) don't have to be renumbered. A TR is one-to-one with a
    // batch (LTBP~CHARG) — surfacing it here means Pallet/Batch never needs
    // to be operator-entered anywhere downstream (scan flow, modal, bulk
    // multi-select all just read row.Batch).
    internal static readonly string[] OpenTrColumns =
        ["DISPO", "TBNUM", "MATNR", "LGORT", "MENGE", "MEINS", "BKTXT", "MBLNR", "BNAME", "BDATU", "BZEIT", "VLTYP", "VLPLA", "BWLVS", "CHARG"];

    internal static RfcRequest BuildOpenTransferRequirementsRequest(OpenTransferRequirementsQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LTBK" })
            .TableRow("QUERY_TABLES", new { TABNAME = "LTBP" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MKPF" });

        builder
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "DISPO" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "TBNUM" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MATNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "LGORT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MENGE" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "MEINS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MKPF", FIELDNAME = "BKTXT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "MBLNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "BNAME" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "BDATU" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "BZEIT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "VLTYP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "VLPLA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBK", FIELDNAME = "BWLVS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LTBP", FIELDNAME = "CHARG" });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "MANDT", TAB_TO = "LTBP", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "LGNUM", TAB_TO = "LTBP", FLD_TO = "LGNUM" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "TBNUM", TAB_TO = "LTBP", FLD_TO = "TBNUM" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MANDT", TAB_TO = "MARC", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "MATNR", TAB_TO = "MARC", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBP", FLD_FROM = "WERKS", TAB_TO = "MARC", FLD_TO = "WERKS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "MANDT", TAB_TO = "MKPF", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "LTBK", FLD_FROM = "MBLNR", TAB_TO = "MKPF", FLD_TO = "MBLNR" });

        builder.WhereCondition($"LTBP~LGNUM EQ '{Warehouse}'");
        builder.WhereCondition("LTBK~STATU <> 'E'"); // Remove TR's already processed
        builder.WhereCondition("LTBK~VLTYP <> '902'"); // Remove TR's relating to GR.
        builder.WhereCondition("LTBP~BESTQ EQ ''"); // Only show TR's for unrestricted stock (not quality blocked)

        if (!string.IsNullOrWhiteSpace(query.MrpController))
            builder.WhereCondition($"MARC~DISPO EQ '{query.MrpController}'");

        if (!string.IsNullOrWhiteSpace(query.Material))
            builder.WhereCondition($"LTBP~MATNR EQ '{SapPad.Pad(query.Material, 18)}'");

        if (!string.IsNullOrWhiteSpace(query.CreatedBy))
            builder.WhereCondition($"LTBK~BNAME EQ '{query.CreatedBy.Trim().ToUpperInvariant()}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static OpenTransferRequirementRow[] ParseOpenTransferRequirementRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= OpenTrColumns.Length)
            .Select(cols => new OpenTransferRequirementRow
            {
                MrpController    = cols[0],
                TrNumber         = cols[1],
                Material         = cols[2],
                StorageLocation  = cols[3],
                Quantity         = RfcRowExtensions.ParseSapDecimal(cols[4]) ?? 0m,
                Uom              = cols[5],
                DocumentText     = cols[6],
                MaterialDocument = cols[7],
                CreatedBy        = cols[8],
                CreatedDate      = cols[9],
                CreatedTime      = cols[10],
                MovementType     = cols[13],
                Batch            = cols[14],
            })
            .ToArray();
    }

    // ── LT04 quality pre-check ──────────────────────────────────────────────────
    //
    // Mirrors create_LT04's own pre-check exactly (ati_code.bas):
    //   sap_rt "LQUA", "CHARG eq '<pnr>' and WERKS eq '3012' and MATNR eq '<matnr>'", "BESTQ", resu
    //   If resu(1) = "Q" Then <refuse — "not scanned out of firewall yet">
    // A single-field LQUA lookup keyed on batch+material+plant, not the
    // usual multi-column StockRow shape — kept separate from
    // BuildStockRequest/ParseStockRows above rather than overloading them.
    internal static RfcRequest BuildQualityBlockCheckRequest(string material, string palletOrBatch)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = "BESTQ" });

        builder
            .WhereCondition($"LQUA~CHARG EQ '{SapPad.Pad(palletOrBatch, 10)}'")
            .WhereCondition($"LQUA~WERKS EQ '{Plant}'")
            .WhereCondition($"LQUA~MATNR EQ '{SapPad.Pad(material, 18)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static bool IsQualityBlocked(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return false;

        var row = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        return row is { Length: > 0 } && row[0] == "Q";
    }

    // ── Create LT04 (create + auto-confirm TO from an open TR) ─────────────────
    //
    // Replicates transaction LT04 screen-for-screen exactly as recorded in
    // wm_lt01.xltm's ati_code module (create_LT04 function) — see that
    // recording for the ground truth this mirrors:
    //   Screen 0131: enter warehouse + TR number, tick ALAKT ("adopt
    //     quantities") and TBELI ("select item") so SAP pulls in the TR's
    //     line straight away.
    //   Screen 0104 (=P+ "create item"): LMEN2=0, then the destination
    //     quantity/type/bin on the item-table row the recording used —
    //     row index 5 (LTAPE-*(5)), preserved exactly as recorded since
    //     this only ever needs to handle a single-item TR, matching this
    //     warehouse's actual 131-movement-generated TRs.
    //   Screen 0104 (=TAH1): commits that item row into the TO being built.
    //   Screen 0102: RL03T-SQUIT = "X" — this is the "Adopt +
    //     confirm" tickbox the operators tick manually in LT04 today, i.e.
    //     the auto-confirm step that makes a second LT12 confirmation
    //     unnecessary. LTAP-ZEUGN (reference) is the batch/pallet number,
    //     or an explicit override when one's supplied — exactly as recorded.
    //   Screen 0104 (=BU): save/post.
    // Response parsing reuses ProductionHelpers.ParseBdcResponse/BdcResponse
    // (the same MESSG-parsing helper every other BDC flow in this codebase
    // uses) rather than a bespoke parser — Type == "S" is success, matching
    // create_LT04's own `Left(mt04_message, 1) = "S"` check.
    internal static RfcRequest BuildCreateLt04Request(CreateLt04Request body)
    {
        var destinationBin = SapPad.Pad(body.DestinationBin, 10);
        var reference       = string.IsNullOrWhiteSpace(body.Reference)
            ? SapPad.Pad(body.PalletOrBatch, 10)
            : body.Reference;

        return BdcBuilder.For("LT04")
            .Screen("SAPML03T", "0131")
                .Field("BDC_OKCODE",  "/00")
                .Field("LTAK-LGNUM",  Warehouse)
                .Field("LTBK-TBNUM",  body.TrNumber)
                .Field("RL03T-ALAKT", "X")
                .Field("RL03T-TBELI", "X")
            .Screen("SAPML03T", "0104")
                .Field("BDC_OKCODE",     "=P+")
                .Field("RL03T-LMEN2",    "0")
                .Field("LTAPE-ANFME(5)", body.Quantity)
                .Field("LTAPE-NLTYP(5)", body.DestinationType)
                .Field("LTAPE-NLPLA(5)", destinationBin)
            .Screen("SAPML03T", "0104")
                .Field("BDC_OKCODE", "=TAH1")
            .Screen("SAPML03T", "0102")
                .Field("BDC_OKCODE",  "/00")
                .Field("RL03T-SQUIT", "X")
                .Field("LTAP-ZEUGN",  reference)
            .Screen("SAPML03T", "0104")
                .Field("BDC_OKCODE", "=BU")
            .Build();
    }

    // ── Consignment MB1B ──────────────────────────────────────────────────────
    //
    // Was a BDC recording of transaction MB1B (see git history for the old
    // screen-field mapping) — replaced with the real BAPI_GOODSMVT_CREATE
    // (GM_CODE "04", movement 411 K) per the user, after a live Nexus
    // consignment issue failed against this exact combination while a
    // hand-typed test.http call for the same data succeeded, pointing at BDC
    // screen-state fragility rather than a data/formatting bug. Field names
    // are taken from BAPI2017_GM_ITEM_CREATE's real component list (MATERIAL,
    // PLANT, STGE_LOC, MOVE_TYPE, SPEC_STOCK, VENDOR, ENTRY_QNT, MOVE_STLOC,
    // MOVE_PLANT), mirroring the old BDC field mapping 1:1:
    //   RM07M-LGORT/MSEGK-UMLGO (issuing == receiving storage location, same
    //     physical location — this is a stock-category change, not a
    //     physical move) -> STGE_LOC / MOVE_STLOC
    //   RM07M-SOBKZ "K"         -> SPEC_STOCK "K"
    //   MSEGK-LIFNR             -> VENDOR
    //   MSEG-MATNR(01)          -> MATERIAL
    //   MSEG-ERFMG(01)          -> ENTRY_QNT
    //   MKPF-BKTXT "Consignment"-> GOODSMVT_HEADER-HEADER_TXT "Consignment"
    //     (body.Header was already ignored by the old BDC too — MKPF-BKTXT
    //     was hardcoded there as well, so this preserves that, not a new gap)
    internal static RfcRequest BuildMb1bRequest(ConsignmentMb1bRequest body)
    {
        var today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var builder = new RfcRequestBuilder(StockAdjustmentHelper.FnGoodsMvtCreate)
            .StructImport("GOODSMVT_HEADER", new
            {
                PSTNG_DATE = today,
                DOC_DATE   = today,
                HEADER_TXT = "Consignment",
            })
            .StructImport("GOODSMVT_CODE", new { GM_CODE = GmCodeTransferPosting })
            .Import("TESTRUN", body.TestRun ? "X" : "");

        builder.TableRow("GOODSMVT_ITEM", new Dictionary<string, object?>
        {
            ["MATERIAL"]   = SapPad.Pad(body.Material, 18),
            ["PLANT"]      = Plant,
            ["STGE_LOC"]   = body.StorageLocation,
            ["MOVE_TYPE"]  = "411",
            ["SPEC_STOCK"] = "K",
            ["VENDOR"]     = SapPad.Pad(body.SpecialStockNumber, 10),
            ["ENTRY_QNT"]  = body.Quantity,
            ["MOVE_STLOC"] = body.StorageLocation,
            ["MOVE_PLANT"] = Plant,
        });

        builder
            .ReadParam("MATERIALDOCUMENT")
            .ReadParam("MATDOCUMENTYEAR")
            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }

    // SourceBin/DestinationBin here come straight from the user-typed bin
    // fields in the consignment transfer UI (warehouse.js), same as the
    // plain (non-consignment) transfer-order flow — padded to 10 digits with
    // SapPad.Pad below for the same reason as BuildTransferOrderRequest's
    // I_VLPLA/I_NLPLA: LTAP-VLPLA/LTAP-NLPLA expect zero-padded numeric bin
    // codes, and an unpadded value here would silently write a bin SAP
    // doesn't recognise into the BDC screen rather than raising a clear
    // error at all.
    //
    // Both LT01 legs below were also BDC recordings, replaced with
    // L_TO_CREATE_SINGLE — the same real RFC BuildTransferOrderRequest
    // already uses for the plain transfer-order endpoint, per the user.
    // That RFC already exposes I_SOBKZ/I_SONUM for exactly this special-stock
    // case (see BuildTransferOrderRequest above), so this is a straight
    // field-for-field port of the old LTAP-*/RL03T-* BDC values onto their
    // I_* import-parameter equivalents, not a new design.
    internal static RfcRequest BuildToNonConsignRequest(ConsignmentMb1bRequest body) =>
        new RfcRequestBuilder(FnCreateTo)
            .Import("I_LGNUM", Warehouse)
            .Import("I_WERKS", Plant)
            .Import("I_LGORT", body.StorageLocation)
            .Import("I_SQUIT", "X")
            .Import("I_BWLVS", "999")
            .Import("I_MATNR", SapPad.Pad(body.Material, 18))
            .Import("I_ANFME", body.Quantity)
            .Import("I_VLTYP", "922")
            .Import("I_VLPLA", "BLOCK")
            .Import("I_NLTYP", body.DestinationType)
            .Import("I_NLPLA", SapPad.Pad(body.DestinationBin, 10))
            .ReadParam("E_TANUM")
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .Build();

    internal static RfcRequest BuildToConsignRequest(ConsignmentMb1bRequest body) =>
        new RfcRequestBuilder(FnCreateTo)
            .Import("I_LGNUM", Warehouse)
            .Import("I_WERKS", Plant)
            .Import("I_LGORT", body.StorageLocation)
            .Import("I_SQUIT", "X")
            .Import("I_BWLVS", "999")
            .Import("I_MATNR", SapPad.Pad(body.Material, 18))
            .Import("I_ANFME", body.Quantity)
            .Import("I_SOBKZ", "K")
            .Import("I_SONUM", SapPad.Pad(body.SpecialStockNumber, 16))
            .Import("I_VLTYP", body.SourceType)
            .Import("I_VLPLA", SapPad.Pad(body.SourceBin, 10))
            .Import("I_NLTYP", "922")
            .Import("I_NLPLA", "BLOCK")
            .ReadParam("E_TANUM")
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .Build();

    /// <summary>Parses just the MB1B (BAPI_GOODSMVT_CREATE) leg — used for the TestRun path, which never reaches the LT01 legs.</summary>
    internal static ConsignmentMb1bResponse ParseMb1bOnly(RfcResponse mb1b)
    {
        var (success, message) = SummarizeGoodsMvtResult(mb1b);
        return new ConsignmentMb1bResponse { Success = success, Mb1bMessage = message };
    }

    internal static bool Mb1bSucceeded(RfcResponse mb1b) => SummarizeGoodsMvtResult(mb1b).Success;

    internal static ConsignmentMb1bResponse ParseConsignmentResponse(
        RfcResponse mb1b, RfcResponse toNonConsign, RfcResponse toConsign)
    {
        // Type "E"/"A" in any leg's RETURN table means SAP rejected that leg
        // (e.g. deficit stock), even though the RFC/BAPI call itself
        // returned normally. Keep checking every leg independently rather
        // than trusting an overall "it didn't throw" — a failed leg that
        // still looked like a success previously masked a consignment issue
        // that never actually posted; see WarehouseController.ConsignmentMb1b.
        var (mb1bSuccess, mb1bMessage)     = SummarizeGoodsMvtResult(mb1b);
        var (toNonCSuccess, toNonCMessage) = SummarizeTransferOrderResult(toNonConsign);
        var (toCSuccess, toCMessage)       = SummarizeTransferOrderResult(toConsign);

        return new ConsignmentMb1bResponse
        {
            Success             = mb1bSuccess && toNonCSuccess && toCSuccess,
            Mb1bMessage         = mb1bMessage,
            ToNonConsignMessage = toNonCMessage,
            ToConsignMessage    = toCMessage
        };
    }

    // BAPI_GOODSMVT_CREATE: business errors surface as TYPE "E"/"A" rows in
    // RETURN with the RFC call itself still returning normally (same
    // convention as StockAdjustmentHelper.ParseStockAdjustmentResponse) — no
    // material document number means no posting happened, regardless of
    // what RETURN says.
    private static (bool Success, string Message) SummarizeGoodsMvtResult(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");
        var matDoc    = ReturnTableHelper.GetParam(response, "MATERIALDOCUMENT") ?? "";
        var success   = !string.IsNullOrWhiteSpace(matDoc) && !ReturnTableHelper.HasBlockingError(messages);

        if (success)
            return (true, $"S Document {matDoc} posted");

        var blocking = messages.FirstOrDefault(m => m.Type is "E" or "A");
        return (false, blocking is not null
            ? $"{blocking.Type} {blocking.Message}"
            : "E MB1B posting did not create a material document.");
    }

    // L_TO_CREATE_SINGLE: same RETURN-table convention as
    // ParseTransferOrderResponse, but — unlike that endpoint — a blocking
    // "E"/"A" message here is treated as a real failure rather than assumed
    // benign, since this consignment flow specifically needs every leg's
    // outcome checked (see ParseConsignmentResponse above).
    private static (bool Success, string Message) SummarizeTransferOrderResult(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");
        var blocking  = messages.FirstOrDefault(m => m.Type is "E" or "A");

        if (blocking is not null)
            return (false, $"{blocking.Type} {blocking.Message}");

        var tanum = ReturnTableHelper.GetParam(response, "E_TANUM") ?? "";
        return (true, string.IsNullOrWhiteSpace(tanum)
            ? "S " + string.Join("; ", messages.Select(m => $"{m.Type} {m.Message}"))
            : $"S Transfer order {tanum} created");
    }

    // ── Set Delivery Weight (ZDEL) ────────────────────────────────────────────
    //
    // Two screen-0100 hits on the same dynpro, exactly as recorded: the first
    // just selects the delivery (BDC_CURSOR on LIKP-VBELN, =SELE), the second
    // fills in the weight/pallet-count fields (BDC_CURSOR on LIKP-ANZPK,
    // =SAVE). GEWEI is always "KG" — the portal only ever records weights in
    // kilograms, so it's hardcoded rather than taking a unit from the caller.
    // BTGEW/NTGEW screen input rejected a plain decimal.ToString() with
    // "Input must be in the format ___.___.___.__~,___" -- confirmed live
    // (endpoint-test-log-2026-08-28-delivery-0082291409.md) this SAP
    // system's screen mask wants comma-decimal (matching every other real
    // quantity value seen from this system this session, e.g. "300,000",
    // "1.297,000"), not BdcBuilder.Field(string,decimal)'s own
    // InvariantCulture period-decimal convention -- and this call was
    // bypassing that overload entirely anyway by calling .ToString()
    // directly (culture-dependent, not even guaranteed invariant).
    private static string FormatZdelWeight(decimal value) =>
        value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    internal static RfcRequest BuildZdelRequest(SetDeliveryWeightRequest body) =>
        BdcBuilder.For("ZDEL")
            .Screen("SAPMZDEL", "0100")
                .Field("BDC_CURSOR", "LIKP-VBELN")
                .Field("BDC_OKCODE", "=SELE")
                .Field("LIKP-VBELN", SapPad.Pad(body.DeliveryNumber, 10))
            .Screen("SAPMZDEL", "0100")
                .Field("BDC_CURSOR", "LIKP-ANZPK")
                .Field("BDC_OKCODE", "=SAVE")
                // Confirmed via a real SHDB recording (2026-08-28): unlike
                // screen 1, VBELN here is NOT zero-padded -- SAP's own UI
                // re-displays the already-selected document in its "natural"
                // (leading-zeros-stripped) numeric form on this screen, and
                // the custom ZDEL program's own internal lookup apparently
                // depends on matching that exact unpadded representation.
                // Sending the padded 10-digit form here (as this code
                // previously did on both screens identically) caused a real,
                // confirmed-live CH 004 "table does not contain an entry"
                // rejection.
                .Field("LIKP-VBELN", body.DeliveryNumber.TrimStart('0'))
                .Field("LIKP-BTGEW", FormatZdelWeight(body.GrossWeight))
                .Field("LIKP-NTGEW", FormatZdelWeight(body.NetWeight))
                .Field("LIKP-GEWEI", "KG")
                .Field("LIKP-ANZPK", body.PalletCount.ToString())
            .Build();

    internal static SetDeliveryWeightResponse ParseZdelResponse(RfcResponse response) =>
        new SetDeliveryWeightResponse
        {
            Message = ReturnTableHelper.GetParam(response, "MESSG") ?? ""
        };
}
