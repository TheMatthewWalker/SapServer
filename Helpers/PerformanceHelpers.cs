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
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreate = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    private static readonly string[] StockColumns = ["MATNR", "CHARG", "LGPLA", "LGTYP", "GESME", "VERME", "LGORT"];

    // ── Stock (LQUA) ─────────────────────────────────────────────────────────
    // Direct port of Get_Stock — same shape as ProductionHelpers.BuildBomRequest,
    // just pointed at LQUA instead of ZBOM_INFO.

    internal static RfcRequest BuildStockRequest()
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" })
            .TableRow("QUERY_TABLES", new { TABNAME = "ZPRODBATCH" });

        foreach (var field in StockColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "ZPRODBATCH", FIELDNAME = "PALL_MATNR" });
        builder.TableItemRow("join_FIELDS", new { TAB_FROM = "LQUA", FLD_FROM = "CHARG", TAB_TO = "ZPRODBATCH", FLD_TO = "CHARG" });

        builder.WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'");
        builder.WhereCondition("LQUA~BESTQ NE 'S'"); // exclude blocked stock
        builder.WhereCondition("LQUA~LGORT EQ '1711'"); // only finished goods

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static PerformanceStockRow[] ParseStockRows(RfcResponse response, Dictionary<string, string>pcList)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 8)
            .Select(cols => new PerformanceStockRow
            {
                Material        = cols[0],
                Batch           = cols[1],
                StorageBin      = cols[2],
                StorageType     = cols[3],
                TotalQty        = decimal.TryParse(cols[4], out var gesme) ? gesme : 0m,
                AvailableQty    = decimal.TryParse(cols[5], out var verme) ? verme : 0m,
                StorageLocation = cols[6],
                PackagingMaterial = cols[7],
                ProfitCentre = pcList.GetValueOrDefault(
                                    NormaliseMaterial(cols[0]), "")
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
            // DATERANGE is typed /SAPNEA/BAPIDATUM, not the usual SIGN/OPTION/LOW/HIGH range —
            // its bounds are DATUM_LOW/DATUM_HIGH. Confirmed against the actual RFM metadata;
            // every other *RANGE table below this one does use plain LOW/HIGH.
            .TableRow("DATERANGE",     new { SIGN = "I", OPTION = "LT", DATUM_LOW = horizonEnd.ToString("yyyyMMdd") })
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
            // Trimmed: WAERK is a fixed-width SAP CHAR field and can come back
            // padded (e.g. "EUR  "). Untrimmed, this currency code is later used
            // both as a Dictionary<string, decimal> key (rateDict) and matched
            // against FCURR from a completely different RFC call/table — if the
            // two sides pad differently, a row can silently miss its rate-dict
            // entry (or hit a different, independently-fetched entry for what's
            // logically the same currency) without ever looking wrong here.
            var cur   = reqInfo is null ? "" : Str(reqInfo, "WAERK").Trim();
            if (cur == "JPY") netpr *= 100; // SAP-side JPY decimal quirk, carried over from the VBA

            rows.Add(new AgreementRow
            {
                ProfitCentre      = Str(mat, "PRCTR"),
                Plant             = Str(mat, "WERKS"),
                Mid               = Str(mat, "WRKST"),
                MrpController     = Str(mat, "DISPO"),
                Material          = NormaliseMaterial(Str(mat, "MATNR")),
                MaterialText      = Str(mat, "MAKTX"),
                ValueStream       = Str(mat, "MDV01"),
                OnHandQty         = onHandByKey.GetValueOrDefault(matKey),
                Uom               = Str(req, "MEINS"),
                StandardPrice     = Dec(mat, "STPRS"),
                LocalCurrency     = Str(mat, "WAERS").Trim(),
                Customer          = reqInfo is null ? NormaliseMaterial(Str(req, "SRC02")) : NormaliseMaterial(Str(reqInfo, "KUNNR")),
                CustomerGroup     = reqInfo is null ? "" : Str(reqInfo, "KONZS"),
                CustomerName      = reqInfo is null ? "" : Str(reqInfo, "NAME1"),
                OrderType         = reqInfo is null ? "" : Str(reqInfo, "AUART"),
                ReferenceDocument = Str(req, "SRC03"), // order while open, delivery once picked
                Item              = reqInfo is null ? "" : Str(reqInfo, "POSNR"),
                CustomerPo        = reqInfo is null ? "" : Str(reqInfo, "BSTNK"),
                CustomerMaterial  = reqInfo is null ? "" : Str(reqInfo, "KDMAT"),
                CustomerReference = reqInfo is null ? "" : Str(reqInfo, "KNREF"),
                UnloadingPoint    = reqInfo is null ? "" : Str(reqInfo, "ABLAD"),
                RequestDate       = DateTime.TryParse(Str(req, "DATUM"), out var date) ? date : DateTime.MinValue,                 
                Week              = Str(req, "WEEK"),
                Period            = Str(req, "PERIOD"),
                OrderQty          = qty,
                Amount            = kpein == 0 ? 0 : qty * (netpr / kpein),
                Currency          = cur,
                LocalAmount       = 1m
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

    internal static InvoiceRow[] ParseInvoiceRows(RfcResponse response, Dictionary<string, string>pcList) =>
        GetRows(response, "SALE_HIST_T")
            .Select(r => new InvoiceRow
            {
                Plant          = Str(r, "WERKS"),
                SalesOrg       = Str(r, "VKORG"),
                InvoiceDate    = DateTime.TryParse(Str(r, "FKDAT"), 
                                    out var date) ? date : DateTime.MinValue,
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
                ProfitCentre   = pcList.GetValueOrDefault(
                                    NormaliseMaterial(Str(r, "MATNR")), 
                                    Str(r, "PRCTR")),
                Period         = Str(r, "PERIOD")
            })
            .ToArray();


    internal static string NormaliseMaterial(string material)
    {
        material = material?.Trim() ?? "";

        // Only strip leading zeros if the entire string is numeric
        return material.All(char.IsDigit)
            ? material.TrimStart('0')
            : material;
    }



    // ── Stock ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildMaterialProfitCentre()
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  "")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "MATNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "PRCTR" })
            .WhereCondition($"MARC~WERKS EQ '{Plant}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    internal static Dictionary<string, string> ParseMaterialProfitCentre(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|')
            .Where(cols => cols.Length >= 2)
            .GroupBy(cols => cols[0].Trim())
            .ToDictionary(
                g => g.Key,
                g => g.First()[1].Trim()
            );

    }




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
                DeliveryDate = DateTime.TryParse(Str(r, "LFDAT"), 
                                    out var date) ? date : DateTime.MinValue,
                DeliveryQty  = Dec(r, "MENGE"),
                Uom          = Str(r, "MEINS"),
                TargetDate   = DateTime.TryParse(Str(r, "TARG1_DT"), 
                                    out var tdate) ? tdate : DateTime.MinValue,
                TargetQty    = Dec(r, "TARG1_ORIG"),
                QtyClass     = Str(r, "QTYCLASS"),
                DateClass    = Str(r, "DATCLASS")
            })
            .ToArray();

    // ── Currency Convertors ────────────────────────────────────────────────────────
    //
    // 
    internal static IEnumerable<RfcRequest> BuildCurrencyRequests(
        IEnumerable<string> curr,
        string localCurrency)
    {
        foreach (var c in curr)
        {
            if (string.IsNullOrEmpty(c))
                continue;

            yield return new RfcRequestBuilder("Z_CURR_RATE_GET")
                .Import("FOREIGN_CURRENCY", c)
                .Import("LOCAL_CURRENCY", localCurrency)
                .Import("TYPE_OF_RATE", "M")
                .Import("VALID_DATE", DateTime.Today.ToString("yyyyMMdd"))
                .ReadTable("CURR_RATE_T", ["FCURR", "UKURS"])
                .Build();
        }
    }

    internal static Dictionary<string, decimal> ParseCurrencyRows(RfcResponse response)
    {
        var dict = new Dictionary<string, decimal>();
        var rows = GetRows(response, "CURR_RATE_T");

        foreach (var r in rows)
        {   // Trimmed for the same reason as AgreementRow.Currency/LocalCurrency
            // above — FCURR is this call's own fixed-width CHAR field and doesn't
            // necessarily pad the same way WAERK/WAERS do on the agreements side.
            // Untrimmed, "EUR" from one call and "EUR " from another end up as
            // two different dictionary keys, and BuildCurrencyRequests can end up
            // issuing more than one Z_CURR_RATE_GET for what's really one
            // currency — each hitting a potentially different pooled SAP session.
            var currency = Str(r, "FCURR").Trim();
            var rawUkurs = r.GetValueOrDefault("UKURS")?.ToString();
            var rate     = Dec(r, "UKURS");
            //Console.WriteLine($"CURR_RATE_T row: FCURR=\"{currency}\" raw UKURS=\"{rawUkurs}\" parsed rate={rate.ToString(CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(currency))
                { dict[currency] = rate; }
        }

        return dict;
    }

  internal static AgreementRow[] ApplyCurrencyConversion(
        AgreementRow[] rows,
        Dictionary<string, decimal> rates)
    {
        foreach (var row in rows) {
            
            //Console.WriteLine($"Row currency: {row.Currency}");
            //Console.WriteLine($"Rate found: {rates.GetValueOrDefault(row.Currency, -1)}");

            if (string.IsNullOrEmpty(row.Currency))
            { row.LocalAmount = row.Amount;
                continue; }

            decimal rate = rates.GetValueOrDefault(row.Currency, 1m);
            row.LocalAmount = row.Amount * rate;

        }
        return rows;
    }


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

    // SAP decimals often arrive in European format ("1.234,56", or a rate like
    // "1,00000") — strip thousands-separator dots and convert the decimal comma
    // to a point before parsing, same normalization as RfcRowExtensions.GetDecimal.
    // Without this, decimal.TryParse (culture-dependent, no NumberStyles override)
    // silently treated the comma as a thousands separator instead of a decimal
    // point, inflating values by ~100,000x — e.g. a currency rate of "1,00000"
    // parsed as 100000 instead of 1.0. That corrupted every non-empty-currency
    // row's LocalAmount via ApplyCurrencyConversion (rate = Dec(r, "UKURS")).
    private static decimal Dec(Dictionary<string, object?> row, string key, decimal fallback = 0m)
    {
        var s = row.GetValueOrDefault(key)?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return fallback;

        s = s.Replace(".", "").Replace(',', '.');
        //Console.WriteLine(s);

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static DateTime? ParseSapDate(object? value) => value switch
    {
        null => null,
        DateTime dt => dt,
        string s when s.Length == 8 && long.TryParse(s, out _) => DateTime.ParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture),
        _ => null
    };

    // ══════════════════════════════════════════════════════════════════════════
    // MM Turns / Valuation Class (mm_turns_valclass.xlsm)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Direct port of the workbook's "get_all" macro and its supporting subs:
    //   Get_marc_mara_makt_mbew  -> BuildMaterialMasterRequest / ParseMaterialMasterRows
    //   main_get_data            -> BuildDemandForecastRequest / ParseDemandForecastRows
    //   Get_mver                 -> BuildConsumptionHistoryRequest / ParseConsumptionHistoryRows
    //   Get_s032                 -> BuildLastMovementRequest / ParseLastMovementRows
    //   Get_T025_T025T_T134      -> BuildValuationClassCatalogRequest / ParseValuationClassCatalogRows
    //   recalc_turns / calc_book_value / make_warnings -> ComputeTurnsRows
    //
    // All four data-pull requests key their results by NormaliseMaterial(MATNR).
    // The master-data call returns MATNR already SAP-padded (e.g. "000000100234");
    // the demand-forecast call returns it via Z_STOCK_REQ_LIST's own MATNR_LIST,
    // which the workbook itself converts to a bare number ("100234") before using
    // it as a lookup key (see main_get_data's "IsNumeric(Replace(MATNR,"D","?"))"
    // line) — so the same normalisation already used for ProfitCentre lookups
    // elsewhere in this file is what makes the two sides joinable here too.
    //
    // Design deviation from the workbook: the VBA drives the final material list
    // from the intersection of the demand-forecast call and the master-data call,
    // deleting rows the demand call didn't return. This port instead treats the
    // master-data call as authoritative and left-joins forecast/history/movement
    // onto it, so a material with stock but no forecast still shows up flagged
    // "No requirement" instead of being silently dropped — see ComputeTurnsRows.

    // Book-value factor by valuation class — carried over verbatim from calc_book_value / update_val_class.
    internal static decimal BookValueFactor(string valuationClass) => valuationClass.Trim() switch
    {
        "3005" or "7905" or "7925" or "7915" => 0.5m,
        "3006" or "7906" or "3007" or "7907" or "7926" or "7927" or "7916" => 0m,
        _ => 1m
    };

    // Turnover-category bucketing — carried over verbatim from recalc_turns (both branches use the same buckets).
    private static string TurnoverCategoryFor(decimal daysInStock) => daysInStock switch
    {
        < 10 => "<10 days",
        >= 10 and <= 30 => "10 - 30 days",
        > 30 and <= 90 => "31 - 90 days",
        > 90 and <= 180 => "91 - 180 days",
        > 180 and <= 360 => "181 - 360 days",
        _ => "More than 360 days"
    };

    // Warning/comment logic — carried over verbatim from make_warnings, including its own quirk:
    // "prototype" (3007/7907/7927) is classified but never referenced by any of the If-conditions,
    // so those materials always get a blank comment regardless of demand/consumption. Note this is
    // a *different* grouping than BookValueFactor's — the workbook itself treats "book value factor"
    // and "warning category" as separate classifications over the same valuation classes.
    private static string BuildWarning(string valuationClass, decimal demand13mo, decimal cons13mo, DateTime? createdDate)
    {
        bool serial = false, service = false, slow = false, obsolete = false, unknown = false;

        switch (valuationClass.Trim())
        {
            case "3000" or "3001" or "7900" or "7920" or "7921" or "7901": serial = true; break;
            case "3004" or "7904" or "7924": service = true; break;
            case "3005" or "7905" or "7925": slow = true; break;
            case "3006" or "7906" or "7926": obsolete = true; break;
            case "3007" or "7907" or "7927": break; // "prototype" in the VBA — deliberately unused below
            default: unknown = true; break;
        }

        var old = createdDate.HasValue && (DateTime.Today - createdDate.Value).TotalDays > 180;

        if (serial && demand13mo == 0 && old) return "No demand & creation date older than 6 months";
        if (serial && demand13mo == 0 && !old) return "No demand. Less than 6 months old material.";
        if (service && demand13mo == 0 && cons13mo == 0 && old) return "No demand. No consumption. Older than 6 months.";
        if (slow && demand13mo == 0 && cons13mo == 0) return "No demand. No consumption. Older than 6 months.";
        if (obsolete && demand13mo > 0) return "Demand exists in next 12 months";
        if (unknown) return "Non standard valuation class";
        return "";
    }

    /// <summary>Valuation classes accepted as a new valuation class by find_materials_to_change — a
    /// hardcoded allow-list in the VBA, distinct from (and narrower than) the full T025 catalog.</summary>
    internal static readonly HashSet<string> ValidNewValuationClasses = new()
    {
        "3000", "3001", "3004", "3005", "3006", "3007",
        "7900", "7901", "7904", "7905", "7906", "7907", "7910",
        "7915", "7916", "7920", "7921", "7924", "7925", "7926", "7927"
    };

    /// <summary>Appends a "TABLE~FIELD IN opt" where-condition plus its value_list rows. No-op if values is empty.</summary>
    private static void AddInFilter(RfcRequestBuilder builder, string table, string field, IReadOnlyCollection<string>? values)
    {
        if (values is null || values.Count == 0) return;

        builder.WhereCondition($"{table}~{field} IN opt");
        foreach (var v in values)
            builder.TableItemRow("value_list", new { TABNAME = table, FIELDNAME = field, SIGN = "I", OPTION = "EQ", LOW = v, HIGH = "" });
    }

    // ── Material Master + Valuation (MARC/MARA/MAKT/MBEW) ───────────────────────
    // Direct port of Get_marc_mara_makt_mbew. Column order must exactly match
    // the query_FIELDS registration order below.

    private static readonly (string Table, string Field)[] MaterialMasterColumns =
    [
        ("MAKT", "MATNR"), ("MAKT", "MAKTX"), ("MARA", "ERSDA"), ("MARA", "MTART"), ("MARA", "MEINS"),
        ("MARC", "WERKS"), ("MARC", "PRCTR"), ("MARC", "LVORM"), ("MARC", "MAABC"), ("MARC", "EKGRP"),
        ("MARC", "DISPO"), ("MBEW", "BKLAS"), ("MARC", "DISLS"), ("MARC", "FXHOR"), ("MARC", "WEBAZ"),
        ("MARC", "DZEIT"), ("MARC", "EISBE"), ("MARC", "SHZET"), ("MARC", "BSTMI"), ("MARC", "BSTMA"),
        ("MARC", "BSTFE"), ("MARC", "BSTRF"), ("MARC", "SOBSL"), ("MARC", "PLIFZ"), ("MBEW", "LBKUM"),
        ("MBEW", "SALK3"), ("MBEW", "STPRS"), ("MBEW", "PEINH")
    ];

    internal static RfcRequest BuildMaterialMasterRequest(TurnsValClassQuery query)
    {
        var plant = string.IsNullOrWhiteSpace(query.Plant) ? Plant : query.Plant;

        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MAKT" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MBEW" });

        foreach (var (table, field) in MaterialMasterColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MANDT", TAB_TO = "MARA", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MATNR", TAB_TO = "MARA", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MANDT", TAB_TO = "MAKT", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MATNR", TAB_TO = "MAKT", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MANDT", TAB_TO = "MBEW", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MATNR", TAB_TO = "MBEW", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "WERKS", TAB_TO = "MBEW", FLD_TO = "BWKEY" });

        builder.WhereCondition($"MARC~WERKS EQ '{plant}'");

        AddInFilter(builder, "MARC", "PRCTR", query.ProfitCentres);
        AddInFilter(builder, "MARC", "MATNR", query.Materials?.Select(m => SapPad.Pad(m, 18)).ToArray());
        AddInFilter(builder, "MARC", "DISPO", query.MrpControllers);
        AddInFilter(builder, "MARA", "MTART", query.MaterialTypes);
        AddInFilter(builder, "MBEW", "BKLAS", query.ValuationClasses);

        builder.ReadTable("data_display");
        return builder.Build();
    }

    /// <summary>Parses the material-master WA rows into field-name-keyed dictionaries so downstream
    /// code can use the same Str()/Dec() helpers as the Z_STOCK_REQ_LIST-backed methods above.</summary>
    internal static List<Dictionary<string, object?>> ParseMaterialMasterRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        var fieldNames = MaterialMasterColumns.Select(c => c.Field).ToArray();

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= fieldNames.Length)
            .Select(cols =>
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < fieldNames.Length; i++)
                    dict[fieldNames[i]] = cols[i];
                return dict;
            })
            .ToList();
    }

    // ── Demand Forecast (Z_STOCK_REQ_LIST, summary mode) ────────────────────────
    // Direct port of main_get_data. Unlike BuildAgreementsRequest (which wants
    // full order-level detail), this sets SUM_ACROSS_SRC01/02/03/DATE/WEEK = X
    // so SAP collapses everything down to one row per material per period —
    // exactly what a 13-month forecast needs and nothing more.

    internal static RfcRequest BuildDemandForecastRequest(TurnsValClassQuery query)
    {
        var plant = string.IsNullOrWhiteSpace(query.Plant) ? Plant : query.Plant;
        var today = DateTime.Today;
        var from  = new DateTime(today.Year, today.Month, 1);
        var to    = from.AddMonths(13).AddDays(-1); // 13 rolling calendar months, current month through +12

        var builder = new RfcRequestBuilder(FnStockReqList)
            .TableRow("WERKSRANGE", new { SIGN = "I", OPTION = "EQ", LOW = plant })
            .TableRow("DATERANGE",  new { SIGN = "I", OPTION = "BT", DATUM_LOW = from.ToString("yyyyMMdd"), DATUM_HIGH = to.ToString("yyyyMMdd") })
            .Import("SUM_ACROSS_SRC01", "X")
            .Import("SUM_ACROSS_SRC02", "X")
            .Import("SUM_ACROSS_SRC03", "X")
            .Import("SUM_ACROSS_DATE",  "X")
            .Import("SUM_ACROSS_WEEK",  "X")
            .Import("ADD_MSLB_QTY",     "X")
            .ReadParam("RC")
            .ReadTable("MATNR_LIST", "MATNR")
            .ReadTable("MATNR_REQ",  "Z_KEY", "QTY", "PERIOD");

        if (query.MaterialTypes is { Length: > 0 })
            foreach (var mt in query.MaterialTypes)
                builder.TableRow("MTARTRANGE", new { SIGN = "I", OPTION = "EQ", LOW = mt });

        if (query.ProfitCentres is { Length: > 0 })
            foreach (var pc in query.ProfitCentres)
                builder.TableRow("PRCTRRANGE", new { SIGN = "I", OPTION = "EQ", LOW = pc });

        if (query.MrpControllers is { Length: > 0 })
            foreach (var d in query.MrpControllers)
                builder.TableRow("DISPORANGE", new { SIGN = "I", OPTION = "EQ", LOW = d });

        if (query.Materials is { Length: > 0 })
            foreach (var m in query.Materials)
                builder.TableRow("MATNRRANGE", new { SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(m, 18) });

        return builder.Build();
    }

    /// <summary>
    /// Maps each MATNR_REQ row to one of 13 rolling monthly buckets (index 0 = 12 months out,
    /// index 12 = the current month), mirroring main_get_data's "pos" arithmetic:
    ///   y = cy      -> pos = m - cm
    ///   y = cy + 1  -> pos = 12 - cm + m
    /// which is the same thing as (y - cy) * 12 + (m - cm), just split into two loops in the VBA.
    /// </summary>
    internal static Dictionary<string, decimal[]> ParseDemandForecastRows(RfcResponse response)
    {
        var matnrList = GetRows(response, "MATNR_LIST");
        var matnrReq  = GetRows(response, "MATNR_REQ");
        var today     = DateTime.Today;
        var result    = new Dictionary<string, decimal[]>();

        foreach (var req in matnrReq)
        {
            if (!int.TryParse(Str(req, "Z_KEY"), out var idx) || idx < 1 || idx > matnrList.Count)
                continue;

            var material = NormaliseMaterial(Str(matnrList[idx - 1], "MATNR"));
            var period   = Str(req, "PERIOD"); // "YYYYMM"
            if (period.Length != 6 || !int.TryParse(period[..4], out var y) || !int.TryParse(period[4..], out var m))
                continue;

            var offset = (y - today.Year) * 12 + (m - today.Month);
            if (offset < 0 || offset > 12) continue;

            if (!result.TryGetValue(material, out var arr))
                result[material] = arr = new decimal[13];

            arr[offset] += Dec(req, "QTY");
        }

        return result;
    }

    // ── Consumption History (MVER) ───────────────────────────────────────────────
    // Direct port of Get_mver. Reads GSV01-12 (monthly goods-issue quantity) and folds
    // them into a 36-slot rolling window (3 years) — wider than the 13-slot window the
    // display chart and turns/days-in-stock calcs use, so the Node-side seasonal-index
    // predicted-usage calculation (see performanceforecast.js) has enough same-calendar-
    // month observations across multiple years to weight a seasonal index. ComputeTurnsRows
    // below derives the original 13-month slice from the tail of this array for every
    // existing calculation, so nothing that already worked changes behaviour.

    private const int HistoryMonths = 36;
    private static readonly string[] MverMonthColumns = [.. Enumerable.Range(1, 12).Select(i => $"GSV{i:00}")];

    // Deliberately NO MATNR filter here. RFC_READ_TABLE builds its WHERE clause from one
    // OPTIONS-table row per IN-list value, and for an unfiltered plant pull that's 15,000+
    // materials — a filter that large made SAP silently return zero rows (no exception),
    // which is what made consumption history always show empty. A batched-calls workaround
    // was tried and reverted: it hammered the shared SAP connection pool (SapStaWorker) with
    // dozens of rapid sequential RFC calls per request, which turned out to disturb *other*
    // concurrent/queued requests on the same worker (confirmed: an unrelated request's
    // material-master row count collapsed from ~16,700 to 1 on the very next test). Simplest
    // and safest fix: pull all of MVER for the plant/year-range in one call — same scale as
    // the unfiltered material-master pull, which already handles ~16,700 rows fine — and let
    // ComputeTurnsRows' NormaliseMaterial(key) dictionary lookup match it in memory, exactly
    // how BuildDemandForecastRequest already works (it doesn't filter by material either).
    internal static RfcRequest BuildConsumptionHistoryRequest(string? plant = null)
    {
        var effectivePlant = string.IsNullOrWhiteSpace(plant) ? Plant : plant;
        var today = DateTime.Today;

        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MVER" });

        foreach (var f in new[] { "MATNR", "WERKS", "GJAHR" }.Concat(MverMonthColumns))
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MVER", FIELDNAME = f });

        builder.WhereCondition($"MVER~WERKS EQ '{effectivePlant}'");

        // GJAHR is SAP's fiscal year, not necessarily the calendar year — a company on
        // an April-March (or any non-calendar-aligned) fiscal variant would have consumption
        // for "this year" posted under a GJAHR that doesn't match DateTime.Today.Year, and a
        // tight filter would silently return zero MVER rows even though real data exists
        // (unlike BuildDemandForecastRequest, which filters Z_STOCK_REQ_LIST by literal
        // calendar dates and has no such dependency). 36 months back from today can span up
        // to 3 calendar years before this one (e.g. today=Jan 2026 -> 35 months ago=Feb 2023),
        // so cast a net of today.Year-4..today.Year+1 — one extra year of slack on each side
        // for fiscal-year misalignment. The offset check in ParseConsumptionHistoryRows
        // already discards anything outside the real 36-month window, so over-fetching here
        // is harmless.
        AddInFilter(builder, "MVER", "GJAHR",
            Enumerable.Range(today.Year - 4, 6).Select(y => y.ToString()).ToArray());

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static Dictionary<string, decimal[]> ParseConsumptionHistoryRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        var today   = DateTime.Today;
        var result  = new Dictionary<string, decimal[]>();
        var minCols = 3 + MverMonthColumns.Length;

        foreach (var cols in SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true))
        {
            if (cols.Length < minCols) continue;

            var material = NormaliseMaterial(cols[0]);
            if (!int.TryParse(cols[2].Trim(), out var year)) continue;

            if (!result.TryGetValue(material, out var arr))
                result[material] = arr = new decimal[HistoryMonths];

            for (var m = 1; m <= 12; m++)
            {
                var offset = (year - today.Year) * 12 + (m - today.Month);
                if (offset < -(HistoryMonths - 1) || offset > 0) continue;

                arr[offset + HistoryMonths - 1] += decimal.TryParse(cols[2 + m].Trim(), out var qty) ? qty : 0m;
            }
        }

        return result;
    }

    // ── Last Movement Dates (S032) ───────────────────────────────────────────────
    // Direct port of Get_s032. One row per storage location in SAP; the workbook
    // takes the MAX date per material across all of a material's storage locations
    // for each of the four date fields — same here.

    private static readonly string[] S032Columns = ["MATNR", "WERKS", "LGORT", "LETZTZUG", "LETZTABG", "LETZTVER", "LETZTBEW"];

    internal sealed record LastMovementInfo(DateTime? LastReceipt, DateTime? LastGoodsIssue, DateTime? LastConsumption, DateTime? LastGoodsMovement);

    // Same reasoning as BuildConsumptionHistoryRequest above — no MATNR filter, pull the
    // whole plant's S032 movement rows in one call and match in memory via NormaliseMaterial(key).
    internal static RfcRequest BuildLastMovementRequest(string? plant = null)
    {
        var effectivePlant = string.IsNullOrWhiteSpace(plant) ? Plant : plant;

        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "S032" });

        foreach (var f in S032Columns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "S032", FIELDNAME = f });

        builder.WhereCondition($"S032~WERKS EQ '{effectivePlant}'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static Dictionary<string, LastMovementInfo> ParseLastMovementRows(RfcResponse response)
    {
        var byMaterial = new Dictionary<string, DateTime?[]>(); // [receipt, goods issue, consumption, goods mvt]

        if (response.Tables.TryGetValue("data_display", out var sapRows))
        {
            foreach (var cols in SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true))
            {
                if (cols.Length < S032Columns.Length) continue;

                var material = NormaliseMaterial(cols[0]);
                if (!byMaterial.TryGetValue(material, out var dates))
                    byMaterial[material] = dates = new DateTime?[4];

                for (var i = 0; i < 4; i++)
                {
                    var parsed = ParseSapGuiDate(cols[3 + i]);
                    if (parsed.HasValue && (!dates[i].HasValue || parsed > dates[i]))
                        dates[i] = parsed;
                }
            }
        }

        return byMaterial.ToDictionary(kv => kv.Key, kv => new LastMovementInfo(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
    }

    private static DateTime? ParseSapGuiDate(string raw)
    {
        raw = raw.Trim();
        return string.IsNullOrEmpty(raw) || raw == "00.00.0000"
            ? null
            : DateTime.TryParseExact(raw, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // ── Consignment Stock (MKOL) ──────────────────────────────────────────────
    // Vendor consignment stock never appears in MBEW (it has no value yet from our
    // accounting perspective — ownership hasn't transferred), so the material-master
    // pull above (Get_marc_mara_makt_mbew) is blind to it entirely, by design. For
    // MRP planning purposes that's a real gap: physically-available quantity for
    // planning needs to include consignment stock even though it must never be
    // treated as valued/owned stock. MKOL (Special Stocks from Vendor, SOBKZ='K')
    // is SAP's standard quantity-only table for exactly this — same pattern as
    // BuildConsumptionHistoryRequest/BuildLastMovementRequest just above: filtered
    // only by plant (no MATNR IN-list), one bulk call, matched to materials in
    // memory via NormaliseMaterial(key) — the lesson from the reverted MVER/S032
    // batching attempt applies here too, and this keeps the SAP-side cost of this
    // feature to exactly one extra unfiltered RFC call per GetTurnsValClass run.
    //
    // Only MKOL-SLABS (unrestricted-use consignment stock) is pulled — the
    // quantity actually usable/available for planning — not SINSM (quality
    // inspection), SSPEM (blocked), or SEINM (restricted-use), which mirrors how
    // MRP conventionally only counts unrestricted stock as available supply.
    // Summed across storage location/batch (MKOL has one row per LGORT/CHARG).

    private static readonly string[] MkolColumns = ["MATNR", "WERKS", "SLABS"];

    internal static RfcRequest BuildConsignmentStockRequest(string? plant = null)
    {
        var effectivePlant = string.IsNullOrWhiteSpace(plant) ? Plant : plant;

        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MKOL" });

        foreach (var f in MkolColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MKOL", FIELDNAME = f });

        builder
            .WhereCondition($"MKOL~WERKS EQ '{effectivePlant}'")
            .WhereCondition("MKOL~SOBKZ EQ 'K'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static Dictionary<string, decimal> ParseConsignmentStockRows(RfcResponse response)
    {
        var result = new Dictionary<string, decimal>();

        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return result;

        foreach (var cols in SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true))
        {
            if (cols.Length < MkolColumns.Length) continue;

            var material = NormaliseMaterial(cols[0]);
            var qty      = decimal.TryParse(cols[2].Trim(), out var v) ? v : 0m;

            result[material] = result.GetValueOrDefault(material) + qty;
        }

        return result;
    }

    // ── Valuation Class Catalog (T025/T025T/T134) ────────────────────────────────
    // Direct port of Get_T025_T025T_T134 — used to populate a "valid new valuation
    // class" dropdown client-side. Restricted to the same material types as the
    // workbook (ROH/HALB/FERT/HIBE/VERP) and English descriptions.

    private static readonly string[] ValClassMaterialTypes = ["ROH", "HALB", "FERT", "HIBE", "VERP"];

    internal static RfcRequest BuildValuationClassCatalogRequest()
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "T025" })
            .TableRow("QUERY_TABLES", new { TABNAME = "T025T" })
            .TableRow("QUERY_TABLES", new { TABNAME = "T134" })
            .TableItemRow("query_FIELDS", new { TABNAME = "T025", FIELDNAME = "BKLAS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "T025", FIELDNAME = "KKREF" })
            .TableItemRow("query_FIELDS", new { TABNAME = "T134", FIELDNAME = "MTART" })
            .TableItemRow("query_FIELDS", new { TABNAME = "T025T", FIELDNAME = "BKBEZ" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "T025", FLD_FROM = "MANDT", TAB_TO = "T134", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "T025", FLD_FROM = "KKREF", TAB_TO = "T134", FLD_TO = "KKREF" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "T025", FLD_FROM = "MANDT", TAB_TO = "T025T", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "T025", FLD_FROM = "BKLAS", TAB_TO = "T025T", FLD_TO = "BKLAS" });

        AddInFilter(builder, "T134", "MTART", ValClassMaterialTypes);
        builder.WhereCondition("T025T~SPRAS EQ 'E'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static ValClassRow[] ParseValuationClassCatalogRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 4)
            .Select(cols => new ValClassRow
            {
                ValuationClass = cols[0],
                AccountRef     = cols[1],
                MaterialType   = cols[2],
                Description    = cols[3]
            })
            .ToArray();
    }

    // ── Turns / Book Value / Warnings (assembled report row) ─────────────────────
    // Direct port of recalc_turns, calc_book_value and make_warnings, reworked
    // from Excel-cell arithmetic into the 13-slot forecast/history arrays above.

    internal static TurnsValClassRow[] ComputeTurnsRows(
        List<Dictionary<string, object?>> materialMasterRows,
        Dictionary<string, decimal[]> demandForecast,
        Dictionary<string, decimal[]> consumptionHistory,
        Dictionary<string, LastMovementInfo> lastMovement,
        int turnMonths,
        bool historyMode,
        Dictionary<string, decimal>? consignmentStock = null)
    {
        turnMonths = Math.Clamp(turnMonths, 1, 12);
        var today   = DateTime.Today;
        var results = new List<TurnsValClassRow>(materialMasterRows.Count);

        foreach (var row in materialMasterRows)
        {
            var rawMaterial = Str(row, "MATNR").Trim();
            var key         = NormaliseMaterial(rawMaterial);
            var forecast    = demandForecast.GetValueOrDefault(key)     ?? new decimal[13];

            // consumptionHistory now carries HistoryMonths (36) slots so the Node-side
            // seasonal-index predicted-usage calc has multiple years of same-calendar-month
            // data to weight. Everything below this point (turns/days-in-stock, the Warning
            // text, and the ConsumptionHistory field itself) only ever needs the same trailing
            // 13-month window (M-12..Current) it always did, so slice that off the tail here
            // once and use it exactly as before — behaviour for anything already relying on
            // this endpoint is unchanged. The full 36-month array is exposed separately below
            // via ConsumptionHistory36 for the forecasting calc to consume.
            var history36   = consumptionHistory.GetValueOrDefault(key) ?? new decimal[HistoryMonths];
            var history     = history36.Skip(HistoryMonths - 13).ToArray();
            lastMovement.TryGetValue(key, out var movement);

            var stockQty       = Dec(row, "LBKUM");
            var consignmentQty = consignmentStock?.GetValueOrDefault(key) ?? 0m;
            var stockValue  = Dec(row, "SALK3");
            var priceUnit   = Dec(row, "PEINH", 1m);
            var unitPrice   = priceUnit == 0 ? 0 : Dec(row, "STPRS") / priceUnit;
            var valClass    = Str(row, "BKLAS");
            var createdDate = ParseSapDate(Str(row, "ERSDA"));

            var bookValue = stockValue * BookValueFactor(valClass);

            decimal? stockTurns = null, daysInStock = null;
            decimal dailyReqValue;
            string turnoverCategory;

            if (!historyMode)
            {
                // recalc_turns, forward branch: plan_days/plan_qty over the next turnMonths months.
                var planDays  = turnMonths * 30 - today.Day;
                var planQty   = forecast.Take(turnMonths).Sum();
                var planValue = planQty * unitPrice;
                dailyReqValue = planDays == 0 ? 0 : planValue / planDays;

                if (planQty > 0)
                {
                    (stockTurns, daysInStock, turnoverCategory) = StockTurnsFor(planQty, planDays, stockQty);
                }
                else
                {
                    var remainingQty = forecast.Skip(turnMonths).Sum();
                    turnoverCategory = remainingQty == 0 ? "No requirement" : "No req. in turnover period";
                }
            }
            else
            {
                // recalc_turns, historical branch: hist_days/hist_qty over the last turnMonths months.
                var histDays  = (turnMonths - 1) * 30 + today.Day;
                var histQty   = history.Skip(13 - turnMonths).Sum();
                var histValue = histQty * unitPrice;
                dailyReqValue = histDays == 0 ? 0 : histValue / histDays;

                if (histQty > 0)
                {
                    (stockTurns, daysInStock, turnoverCategory) = StockTurnsFor(histQty, histDays, stockQty);
                }
                else
                {
                    turnoverCategory = "No historic req. in turnover period";
                }
            }

            results.Add(new TurnsValClassRow
            {
                Material               = rawMaterial,
                MaterialText           = Str(row, "MAKTX"),
                CreatedDate            = createdDate,
                MaterialType           = Str(row, "MTART"),
                Uom                    = Str(row, "MEINS"),
                Plant                  = Str(row, "WERKS"),
                ProfitCentre           = Str(row, "PRCTR"),
                DeletionFlag           = Str(row, "LVORM") == "X",
                AbcIndicator           = Str(row, "MAABC"),
                PurchasingGroup        = Str(row, "EKGRP"),
                MrpController          = Str(row, "DISPO"),
                ValuationClass         = valClass,
                LotSizeProcedure       = Str(row, "DISLS"),
                PlanningTimeFence      = Dec(row, "FXHOR"),
                GrProcessingTime       = Dec(row, "WEBAZ"),
                TotalReplenishmentTime = Dec(row, "DZEIT"),
                SafetyStock            = Dec(row, "EISBE"),
                MinLotSize             = Dec(row, "BSTMI"),
                MaxLotSize             = Dec(row, "BSTMA"),
                FixedLotSize           = Dec(row, "BSTFE"),
                RoundingValue          = Dec(row, "BSTRF"),
                SpecialProcurementType = Str(row, "SOBSL"),
                PlannedDeliveryTime    = Dec(row, "PLIFZ"),
                StockQty               = stockQty,
                ConsignmentQty         = consignmentQty,
                StockValue             = stockValue,
                UnitPrice              = unitPrice,
                BookValue              = bookValue,
                DemandForecast         = forecast,
                ConsumptionHistory     = history,
                ConsumptionHistory36   = history36,
                LastReceiptDate        = movement?.LastReceipt,
                LastGoodsIssueDate     = movement?.LastGoodsIssue,
                LastConsumptionDate    = movement?.LastConsumption,
                LastGoodsMovementDate  = movement?.LastGoodsMovement,
                StockTurns             = stockTurns,
                DaysInStock            = daysInStock,
                DailyRequirementValue  = dailyReqValue,
                TurnoverCategory       = turnoverCategory,
                Warning                = BuildWarning(valClass, forecast.Sum(), history.Sum(), createdDate)
            });
        }

        return results.ToArray();
    }

    /// <summary>Shared turns/days-in-stock/category calc used by both the forward and historical
    /// branches of recalc_turns — identical formula, just fed plan_qty/plan_days or hist_qty/hist_days.</summary>
    private static (decimal? turns, decimal? days, string category) StockTurnsFor(decimal qty, int periodDays, decimal stockQty)
    {
        if (stockQty > 0)
        {
            var turns = periodDays == 0 ? 0m : (qty / periodDays * 360) / stockQty;
            var days  = turns == 0 ? 0m : 360 / turns;
            return (turns, days, TurnoverCategoryFor(days));
        }

        return stockQty == 0
            ? (null, null, "No stock")
            : (null, null, "Neg. stock");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Valuation Class Change (update_val_class) — WRITES to SAP
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Direct port of update_val_class and its helpers (find_materials_to_change,
    // Get_MARC_MARD, Get_MARD_MCHB, create_MB1A, create_MM02, create_SM30).
    // Moves stock out to an order (MB1A 291), changes the valuation class
    // (MM02), then moves stock back in (MB1A 292). The PID-check popup that
    // MM02 can trigger is suppressed for the duration via SM30 — same as the
    // VBA, which disables it up front specifically to avoid that popup (see
    // its own "V_2_7 pid chk is turned off... Don't need the pop up" comment).
    //
    // Simplification vs. the VBA: create_MB1A packs up to 7 materials per
    // screen across up to 14 screens in a single BDC session (an artifact of
    // minimizing SAP GUI round-trips). This port posts one MB1A call per stock
    // line instead — functionally equivalent, and consistent with how the rest
    // of this codebase already does BDC posting (see ProductionController's
    // scrap-posting loop, one MB11 call per BOM component). The BDC screen
    // sequence below has not been verified against a live MB1A/MM02 recording
    // and should be checked against SM35/a real transaction before first use.

    private static readonly string[] MardCheckColumns =
        ["MATNR", "LGORT", "SPERR", "LABST", "UMLME", "INSME", "EINME", "SPEME", "RETME"];

    internal sealed record MardCheckRow(
        string Material, string StorageLocation, string StockTakeIndicator,
        decimal Unrestricted, decimal TransferQty, decimal QualityInspection,
        decimal RestrictedUse, decimal Blocked, decimal BlockedReturns);

    internal sealed record StockMovementLine(string Material, string Plant, string StorageLocation, decimal Quantity, string Uom, string? Batch);

    // ── Pre-checks (find_materials_to_change) ────────────────────────────────────

    internal static RfcRequest BuildMardCheckRequest(IEnumerable<string> materials, string plant)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARD" });

        foreach (var f in MardCheckColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = f });

        builder.WhereCondition($"MARD~WERKS EQ '{plant}'");
        AddInFilter(builder, "MARD", "MATNR", materials.Select(m => SapPad.Pad(m, 18)).ToArray());

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static List<MardCheckRow> ParseMardCheckRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        static decimal N(string s) => decimal.TryParse(s.Trim(), out var v) ? v : 0m;

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(c => c.Length >= MardCheckColumns.Length)
            .Select(c => new MardCheckRow(
                NormaliseMaterial(c[0]), c[1].Trim(), c[2].Trim(),
                N(c[3]), N(c[4]), N(c[5]), N(c[6]), N(c[7]), N(c[8])))
            .ToList();
    }

    /// <summary>
    /// All-or-nothing pre-check mirroring find_materials_to_change: validates the requested new
    /// valuation classes against the hardcoded allow-list and against each material's current
    /// valuation class, then flags any storage location with an active stock take or with stock
    /// sitting outside unrestricted-use. Returns an empty list when the batch is safe to proceed.
    /// </summary>
    internal static List<string> ValidateValuationClassChanges(
        List<ValClassChangeItem> changes,
        Dictionary<string, string> currentValuationClassByMaterial,
        List<MardCheckRow> mardRows)
    {
        var errors = new List<string>();

        foreach (var change in changes)
        {
            var key = NormaliseMaterial(change.Material);
            var newClass = change.NewValuationClass.Trim();

            if (!ValidNewValuationClasses.Contains(newClass))
            {
                errors.Add($"{change.Material}: '{newClass}' is not a recognised valuation class.");
                continue;
            }

            if (!currentValuationClassByMaterial.TryGetValue(key, out var current))
            {
                errors.Add($"{change.Material}: material not found in plant master data.");
                continue;
            }

            if (current == newClass)
                errors.Add($"{change.Material}: new valuation class is the same as the current one ({current}).");
        }

        var requestedKeys = changes.Select(c => NormaliseMaterial(c.Material)).ToHashSet();

        foreach (var mard in mardRows.Where(m => requestedKeys.Contains(m.Material)))
        {
            if (!string.IsNullOrWhiteSpace(mard.StockTakeIndicator))
                errors.Add($"{mard.Material}: stock take is active at storage location {mard.StorageLocation}.");

            var notUnrestricted = mard.TransferQty + mard.QualityInspection + mard.RestrictedUse + mard.Blocked + mard.BlockedReturns;
            if (notUnrestricted != 0)
                errors.Add($"{mard.Material}: not all stock is unrestricted at storage location {mard.StorageLocation} ({notUnrestricted}).");
        }

        return errors;
    }

    // ── Stock lines to move (Get_MARC_MARD / Get_MARD_MCHB) ─────────────────────

    internal static RfcRequest BuildNonBatchStockRequest(IEnumerable<string> materials, string plant)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARD" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "MATNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "WERKS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = "LGORT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = "LABST" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = "MEINS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MANDT", TAB_TO = "MARD", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MATNR", TAB_TO = "MARD", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "WERKS", TAB_TO = "MARD", FLD_TO = "WERKS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MANDT", TAB_TO = "MARA", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARC", FLD_FROM = "MATNR", TAB_TO = "MARA", FLD_TO = "MATNR" });

        builder.WhereCondition($"MARC~WERKS EQ '{plant}'");
        builder.WhereCondition("MARC~XCHAR NE 'X'"); // not batch-managed
        builder.WhereCondition("MARD~LABST NE 0");
        AddInFilter(builder, "MARC", "MATNR", materials.Select(m => SapPad.Pad(m, 18)).ToArray());

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static RfcRequest BuildBatchStockRequest(IEnumerable<string> materials, string plant)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARD" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MCHB" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = "MATNR" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = "WERKS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARD", FIELDNAME = "LGORT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MCHB", FIELDNAME = "CLABS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = "MEINS" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MCHB", FIELDNAME = "CHARG" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "MANDT", TAB_TO = "MCHB", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "MATNR", TAB_TO = "MCHB", FLD_TO = "MATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "WERKS", TAB_TO = "MCHB", FLD_TO = "WERKS" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "LGORT", TAB_TO = "MCHB", FLD_TO = "LGORT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "MANDT", TAB_TO = "MARA", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MARD", FLD_FROM = "MATNR", TAB_TO = "MARA", FLD_TO = "MATNR" });

        builder.WhereCondition($"MARD~WERKS EQ '{plant}'");
        builder.WhereCondition("MARD~LABST NE 0");
        builder.WhereCondition("MCHB~CLABS NE 0");
        AddInFilter(builder, "MARD", "MATNR", materials.Select(m => SapPad.Pad(m, 18)).ToArray());

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static List<StockMovementLine> ParseNonBatchStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(c => c.Length >= 5)
            .Select(c => new StockMovementLine(
                NormaliseMaterial(c[0]), c[1].Trim(), c[2].Trim(),
                decimal.TryParse(c[3].Trim(), out var qty) ? qty : 0m, c[4].Trim(), null))
            .Where(l => l.Quantity != 0)
            .ToList();
    }

    internal static List<StockMovementLine> ParseBatchStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(c => c.Length >= 6)
            .Select(c => new StockMovementLine(
                NormaliseMaterial(c[0]), c[1].Trim(), c[2].Trim(),
                decimal.TryParse(c[3].Trim(), out var qty) ? qty : 0m, c[4].Trim(), c[5].Trim()))
            .Where(l => l.Quantity != 0)
            .ToList();
    }

    // ── BDC: MB1A goods movement (create_MB1A) ───────────────────────────────────

    internal static RfcRequest BuildMb1aRequest(string movementType, string order, StockMovementLine line)
    {
        var mvt = movementType;
        var qty = line.Quantity;

        // Mirrors the VBA's own sign-swap: a negative on-hand quantity posts the reverse movement type.
        if (qty < 0)
        {
            mvt = movementType == "291" ? "292" : movementType == "292" ? "291" : movementType;
            qty = Math.Abs(qty);
        }

        return BdcBuilder.For("MB1A")
            .Screen("SAPMM07M", "0400")
                .Field("BDC_OKCODE",    "/00")
                .Field("RM07M-BWARTWA", mvt)
                .Field("RM07M-WERKS",   line.Plant)
                .Field("XFULL",         " ")
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE",    "/00")
                .Field("COBL-AUFNR",    order)
            .Screen("SAPMM07M", "0421")
                .Field("MSEG-MATNR(01)", SapPad.Pad(line.Material, 18).ToUpperInvariant())
                .Field("MSEG-ERFMG(01)", qty)
                .Field("MSEG-ERFME(01)", line.Uom)
                .Field("MSEG-LGORT(01)", line.StorageLocation)
                .FieldIf(!string.IsNullOrEmpty(line.Batch), "MSEG-CHARG(01)", line.Batch ?? "")
                .Field("BDC_OKCODE",     "=BU")
            .Build();
    }

    // ── BDC: MM02 valuation class change (create_MM02) ───────────────────────────

    internal static RfcRequest BuildMm02ValuationClassRequest(string material, string plant, string newValuationClass) =>
        BdcBuilder.For("MM02")
            .Screen("SAPLMGMM", "0060")
                .Field("BDC_OKCODE",  "/00")
                .Field("RMMG1-MATNR", SapPad.Pad(material, 18).ToUpperInvariant())
            .Screen("SAPLMGMM", "0070")
                .Field("BDC_OKCODE", "=ENTR")
                .Field("MSICHTAUSW-KZSEL(01)", "X") // "Accounting 1" view checkbox
            .Screen("SAPLMGMM", "3005")
                .Field("BDC_OKCODE", "=SP16") // skip "views already maintained for other plants" popup
            .Screen("SAPLMGMM", "0081")
                .Field("BDC_OKCODE", "=ENTR")
                .Field("RMMG1-WERKS", plant)
            .Screen("SAPLMGMM", "3002")
                .Field("BDC_OKCODE",  "=BU")
                .Field("MBEW-BKLAS",  newValuationClass)
            .Build();

    // ── BDC: SM30 PID-check toggle (create_SM30) ─────────────────────────────────

    internal static RfcRequest BuildPidCheckToggleRequest(bool active) =>
        BdcBuilder.For("SM30")
            .Screen("SAPMSVMA", "0100")
                .Field("BDC_OKCODE", "=UPD")
                .Field("VIEWNAME",   "ZMM_PID_CHECK")
            .Screen("SAPLZTAB", "0026")
                .Field("BDC_OKCODE", "=SAVE")
                .Field("ZMM_PID_CHECK-ACTIVE", active ? "X" : " ")
            .Screen("SAPLZTAB", "0026")
                .Field("BDC_OKCODE", "=UEBE")
            .Screen("SAPMSVMA", "0100")
                .Field("BDC_OKCODE", "/EBACK")
            .Build();

    // ── Controller-facing convenience: current master data by material ──────────

    internal sealed record MaterialSnapshot(string MaterialText, string ValuationClass, decimal StockQty, decimal StockValue);

    /// <summary>Indexes ParseMaterialMasterRows output by NormaliseMaterial(MATNR) — used by the
    /// change-valuation-class endpoint to look up each requested material's current state.</summary>
    internal static Dictionary<string, MaterialSnapshot> IndexMaterialMasterRows(List<Dictionary<string, object?>> rows)
    {
        var dict = new Dictionary<string, MaterialSnapshot>();
        foreach (var row in rows)
            dict[NormaliseMaterial(Str(row, "MATNR"))] = new MaterialSnapshot(
                Str(row, "MAKTX"), Str(row, "BKLAS"), Dec(row, "LBKUM"), Dec(row, "SALK3"));
        return dict;
    }
}