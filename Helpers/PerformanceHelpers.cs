using SapServer.Models;
using System.Globalization;

namespace SapServer.Helpers;

internal static class PerformanceHelpers
{
    // Also used as permission keys, same convention as ProductionHelpers.FnCreate — the
    // permission check is literally "can this user call this SAP function module".
    internal const string FnStockReqList  = "Z_STOCK_REQ_LIST";
    internal const string FnSaleAnalHist  = "Z_SALE_ANAL_HIST";
    internal const string FnCustIndexAnal = "Z_CUST_INDEX_ANALYSE";

    private static readonly string[] StockColumns = ["MATNR", "CHARG", "LGPLA", "GESME", "VERME", "LGORT"];

    // ── Stock (LQUA) ─────────────────────────────────────────────────────────
    // Direct port of Get_Stock — same shape as ProductionHelpers.BuildBomRequest,
    // just pointed at LQUA instead of ZBOM_INFO.

    internal static RfcRequest BuildStockRequest()
    {
        var builder = new RfcRequestBuilder(ProductionHelpers.FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" });

        foreach (var field in StockColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.WhereCondition($"LQUA~LGNUM EQ '{ProductionHelpers.Warehouse}'");
        builder.WhereCondition("LQUA~BESTQ NE 'S'"); // exclude blocked stock — mirrors Get_Stock's add_wc filter

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static StockRow[] ParseStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= StockColumns.Length)
            .Select(cols => new StockRow
            {
                Material        = cols[0],
                Batch           = cols[1],
                StorageBin      = cols[2],
                TotalQty        = decimal.TryParse(cols[3], out var gesme) ? gesme : 0m,
                AvailableQty    = decimal.TryParse(cols[4], out var verme) ? verme : 0m,
                StorageLocation = cols[5]
            })
            .ToArray();
    }

    // ── Agreements (Z_STOCK_REQ_LIST) ───────────────────────────────────────
    // Direct port of get_agreements. SAP hands back four related tables rather
    // than one flat list:
    //   MATNR_LIST      one row per material
    //   MATNR_REQ       one row per requirement, keyed to MATNR_LIST via Z_KEY
    //   MATNR_REQ_INFO  order-specific detail, keyed via Z_KEY2 ("00000" = none)
    //   MATNR_INV       on-hand stock per material
    // The VBA joins these client-side in a loop; ParseAgreementRows does the same.

    internal static RfcRequest BuildAgreementsRequest(DateTime horizonEnd) =>
        new RfcRequestBuilder(FnStockReqList)
            .TableRow("SALESORGRANGE", new { SIGN = "I", OPTION = "EQ", LOW = "3012" })
            .TableRow("WERKSRANGE",    new { SIGN = "I", OPTION = "EQ", LOW = "3012" })
            .TableRow("MTARTRANGE",    new { SIGN = "I", OPTION = "EQ", LOW = "FERT" })
            .TableRow("MTARTRANGE",    new { SIGN = "I", OPTION = "EQ", LOW = "HALB" })
            .TableRow("DATERANGE",     new { SIGN = "I", OPTION = "LT", LOW = horizonEnd.ToString("yyyyMMdd") })
            .Import("SHOW_MATNR_REQ", "X")
            .Import("SHOW_MATNR_INV", "X")
            .Import("ADD_MATNR_REQ_INFO", "X")
            .Import("ADJ_W_GR_DAYS", "X")
            .Import("USE_MKAL_WORKCENTER", "X")
            .ReadParam("RC")
            .ReadTable("MATNR_LIST",     "PRCTR", "MATNR", "WERKS", "MAKTX", "WRKST", "MTART", "DISPO", "STPRS", "WAERS", "MDV01")
            .ReadTable("MATNR_REQ",      "Z_KEY", "Z_KEY2", "QTY", "MEINS", "DATUM", "WEEK", "PERIOD", "SRC01", "SRC02", "SRC03")
            .ReadTable("MATNR_REQ_INFO", "POSNR", "NETPR", "WAERK", "KPEIN", "KDMAT", "KONZS", "KUNNR", "NAME1", "AUART", "BSTNK", "KNREF", "ABLAD")
            .ReadTable("MATNR_INV",      "Z_KEY", "SRC02", "QTY")
            .Build();

    internal static AgreementRow[] ParseAgreementRows(RfcResponse response)
    {
        var matnrList     = GetRows(response, "MATNR_LIST");
        var matnrReq      = GetRows(response, "MATNR_REQ");
        var matnrReqInfo  = GetRows(response, "MATNR_REQ_INFO");
        var matnrInv      = GetRows(response, "MATNR_INV");

        // On-hand stock per material, summing only 'Unrestr' rows — mirrors the VBA's onhand()
        // array, keyed by Z_KEY (MATNR_LIST's row position).
        var onHandByKey = matnrInv
            .Where(r => Str(r, "SRC02") == "Unrestr")
            .GroupBy(r => Str(r, "Z_KEY"))
            .ToDictionary(g => g.Key, g => g.Sum(r => Dec(r, "QTY")));

        var rows = new List<AgreementRow>();

        foreach (var req in matnrReq)
        {
            var matKey = Str(req, "Z_KEY");
            var reqKey = Str(req, "Z_KEY2");

            // MATNR_LIST.Rows(matkey) in the VBA is a 1-indexed row lookup — confirm this still
            // holds for however your connector exposes row ordinals for these tables.
            var mat = int.TryParse(matKey, out var matIdx) && matIdx >= 1 && matIdx <= matnrList.Count
                ? matnrList[matIdx - 1]
                : null;
            if (mat is null) continue;

            Dictionary<string, object?>? reqInfo = reqKey != "00000" && int.TryParse(reqKey, out var reqIdx)
                && reqIdx >= 1 && reqIdx <= matnrReqInfo.Count
                ? matnrReqInfo[reqIdx - 1]
                : null;

            var qty   = Dec(req, "QTY");
            var netpr = reqInfo is null ? 0m : Dec(reqInfo, "NETPR");
            var kpein = reqInfo is null ? 1m : Dec(reqInfo, "KPEIN", 1m);
            var cur   = reqInfo is null ? "" : Str(reqInfo, "WAERK");
            if (cur == "JPY") netpr *= 100; // SAP-side JPY decimal quirk, carried over from the VBA

            rows.Add(new AgreementRow
            {
                ProfitCentre      = Str(mat, "PRCTR"),
                Plant             = Str(mat, "WERKS"),
                Mid               = Str(mat, "WRKST"),
                MrpController     = Str(mat, "DISPO"),
                Material          = Str(mat, "MATNR"),
                MaterialText      = Str(mat, "MAKTX"),
                ValueStream       = Str(mat, "MDV01"),
                OnHandQty         = onHandByKey.GetValueOrDefault(matKey),
                Uom               = Str(req, "MEINS"),
                StandardPrice     = Dec(mat, "STPRS"),
                LocalCurrency     = Str(mat, "WAERS"),
                Customer          = reqInfo is null ? Str(req, "SRC02") : Str(reqInfo, "KUNNR"),
                CustomerGroup     = reqInfo is null ? "" : Str(reqInfo, "KONZS"),
                CustomerName      = reqInfo is null ? "" : Str(reqInfo, "NAME1"),
                OrderType         = reqInfo is null ? "" : Str(reqInfo, "AUART"),
                ReferenceDocument = Str(req, "SRC03"), // order while open, delivery once picked
                Item              = reqInfo is null ? "" : Str(reqInfo, "POSNR"),
                CustomerPo        = reqInfo is null ? "" : Str(reqInfo, "BSTNK"),
                CustomerMaterial  = reqInfo is null ? "" : Str(reqInfo, "KDMAT"),
                CustomerReference = reqInfo is null ? "" : Str(reqInfo, "KNREF"),
                UnloadingPoint    = reqInfo is null ? "" : Str(reqInfo, "ABLAD"),
                RequestDate       = ParseSapDate(req.GetValueOrDefault("DATUM")) ?? default,
                Week              = Str(req, "WEEK"),
                Period            = Str(req, "PERIOD"),
                OrderQty          = qty,
                Amount            = kpein == 0 ? 0 : qty * (netpr / kpein),
                Currency          = cur
            });
        }

        return rows.ToArray();
    }

    // ── Invoicing (Z_SALE_ANAL_HIST) ─────────────────────────────────────────
    // SALE_HIST_T doubles as both the selection-criteria table (rows added
    // before the call, one populated field per row, e.g. {VKORG = "3012"}) and
    // the result table after the call returns — that dual use is unusual but
    // is exactly what the VBA does (Display_sap reads the same table object it
    // built selections into).

    internal static RfcRequest BuildInvoicingRequest(DateTime fromDate, DateTime toDate) =>
        new RfcRequestBuilder(FnSaleAnalHist)
            .TableRow("SALE_HIST_T", new { VKORG = "3012" })
            .TableRow("SALE_HIST_T", new { WERKS = "3012" })
            .Import("FKDAT_FROM", fromDate.ToString("yyyyMMdd"))
            .Import("FKDAT_TO", toDate.ToString("yyyyMMdd"))
            .Import("SUM_MATNR", "N")
            .Import("SUM_EXTWG", "N")
            .Import("SUM_KUNAG", "N")
            .Import("SUM_KONSZ", "N")
            .Import("SUM_DATE", "N")
            .Import("SUM_PERIOD", "N")
            .Import("ADD_COST_INFO", "N")
            .ReadTable("SALE_HIST_T",
                "WERKS", "VKORG", "FKDAT", "FKART", "VBELN", "VGBEL", "AUBEL", "AUPOS",
                "BSTKD", "KONZS", "KUNAG", "MATNR", "ARKTX", "FKIMG", "FNETWR", "LNETWR",
                "WAERK", "PRCTR", "PERIOD")
            .Build();

    internal static InvoiceRow[] ParseInvoiceRows(RfcResponse response) =>
        GetRows(response, "SALE_HIST_T")
            .Select(r => new InvoiceRow
            {
                Plant          = Str(r, "WERKS"),
                SalesOrg       = Str(r, "VKORG"),
                InvoiceDate    = ParseSapDate(r.GetValueOrDefault("FKDAT")) ?? default,
                InvoiceType    = Str(r, "FKART"),
                InvoiceNumber  = Str(r, "VBELN"),
                DeliveryNote   = Str(r, "VGBEL"),
                SalesAgreement = Str(r, "AUBEL"),
                SalesItem      = Str(r, "AUPOS"),
                CustomerPo     = Str(r, "BSTKD"),
                CustomerGroup  = Str(r, "KONZS"),
                Customer       = Str(r, "KUNAG"),
                Material       = Str(r, "MATNR"),
                MaterialText   = Str(r, "ARKTX"),
                Quantity       = Dec(r, "FKIMG"),
                DocumentAmount = Dec(r, "FNETWR"),
                LocalAmount    = Dec(r, "LNETWR"),
                Currency       = Str(r, "WAERK"),
                ProfitCentre   = Str(r, "PRCTR"),
                Period         = Str(r, "PERIOD")
            })
            .ToArray();

    // ── OTIF (Z_CUST_INDEX_ANALYSE) ──────────────────────────────────────────

    internal static RfcRequest BuildOtifRequest(DateTime fromDate, DateTime toDate) =>
        new RfcRequestBuilder(FnCustIndexAnal)
            .StructImport("DEFAULT_RULE", new
            {
                QTYSCORE = 0.5m, QTYREDUU = 0.5m, QTYREDUO = 0.5m,
                DATSCORE = 0.5m, DATREDUL = 0.5m, DATREDUE = 0.5m
            })
            .TableRow("WERKS_SEL", new { SIGN = "I", OPTION = "EQ", LOW = ProductionHelpers.Plant })
            .TableRow("LFDAT_SEL", new { SIGN = "I", OPTION = "BT", LOW = fromDate.ToString("yyyyMMdd"), HIGH = toDate.ToString("yyyyMMdd") })
            .ReadTable("IT_CUST_IDX",
                "KUNNR", "NAME1", "WERKS", "PRCTR", "MATNR", "MAKTX",
                "VBELN", "LFDAT", "MENGE", "MEINS",
                "TARG1_DT", "TARG1_ORIG", "QTYCLASS", "DATCLASS")
            .Build();

    internal static OtifRow[] ParseOtifRows(RfcResponse response) =>
        GetRows(response, "IT_CUST_IDX")
            .Select(r => new OtifRow
            {
                Customer     = Str(r, "KUNNR"),
                CustomerName = Str(r, "NAME1"),
                Plant        = Str(r, "WERKS"),
                ProfitCentre = Str(r, "PRCTR"),
                Material     = Str(r, "MATNR"),
                MaterialText = Str(r, "MAKTX"),
                Delivery     = Str(r, "VBELN"),
                DeliveryDate = ParseSapDate(r.GetValueOrDefault("LFDAT")) ?? default,
                DeliveryQty  = Dec(r, "MENGE"),
                Uom          = Str(r, "MEINS"),
                TargetDate   = ParseSapDate(r.GetValueOrDefault("TARG1_DT")) ?? default,
                TargetQty    = Dec(r, "TARG1_ORIG"),
                QtyClass     = Str(r, "QTYCLASS"),
                DateClass    = Str(r, "DATCLASS")
            })
            .ToArray();

    // ── Shared helpers ────────────────────────────────────────────────────────
    //
    // GetRows is the one assumption point in this file: it assumes that when you
    // call .ReadTable(name, fields...) — as opposed to the WA-only .ReadTable(name)
    // used for ZRFC_READ_TABLES above — your connection layer hands back
    // response.Tables[name] as a List<Dictionary<string, object?>>, one dictionary
    // per row, keyed by the field names you asked for. That's the natural mirror of
    // how RfcRequestBuilder.TableRow/TableItemRow accept rows on the way in. None of
    // the existing code you've shown me exercises this path (everything so far uses
    // the WA-only ZRFC_READ_TABLES route), so if your actual RfcResponse shapes this
    // differently, this is the only method that needs to change — everything above
    // it (the Build*Request methods) doesn't depend on this assumption at all.

    private static List<Dictionary<string, object?>> GetRows(RfcResponse response, string table) =>
        response.Tables.TryGetValue(table, out var rows) ? rows : [];

    private static string Str(Dictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key)?.ToString() ?? "";

    private static decimal Dec(Dictionary<string, object?> row, string key, decimal fallback = 0m) =>
        decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var v) ? v : fallback;

    private static DateTime? ParseSapDate(object? value) => value switch
    {
        null => null,
        DateTime dt => dt,
        string s when s.Length == 8 && long.TryParse(s, out _) => DateTime.ParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture),
        _ => null
    };
}
