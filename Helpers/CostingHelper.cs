using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class CostingHelper
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";
    internal const string FnPeriodBalances = "BAPI_GL_GETGLACCPERIODBALANCES";
    internal const string Plant        = "3012";
    internal const string CompanyCode        = "0312";
    internal const string StatusFilter = "FR";

    internal static readonly (string Table, string Field)[] CostSheetFields =
    [
        ("ZCOST_INFO3", "MATNR"),
        ("ZCOST_INFO3", "WERKS"),
        ("ZCOST_INFO3", "KADAT"),
        ("ZCOST_INFO3", "BIDAT"),
        ("ZCOST_INFO3", "PRCTR"),
        ("ZCOST_INFO3", "BUKRS"),
        ("ZCOST_INFO3", "PATNR"),
        ("ZCOST_INFO3", "KST001"),
        ("ZCOST_INFO3", "KST008"),
        ("ZCOST_INFO3", "KST017"),
        ("ZCOST_INFO3", "KST002"),
        ("ZCOST_INFO3", "KST004"),
        ("ZCOST_INFO3", "KST019"),
        ("ZCOST_INFO3", "KST006"),
        ("ZCOST_INFO3", "KST033"),
        ("ZCOST_INFO3", "LOSGR"),
        ("ZCOST_INFO3", "MEINS"),
        ("ZCOST_INFO3", "FEH_STA"),
        ("PATN",        "WERK"),
        ("ZCOST_SHEET", "VALID_FROM"),
        ("ZCOST_SHEET", "VALID_TO"),
        ("ZCOST_SHEET", "OH_PCT"),
        ("ZCOST_SHEET", "IC_MARK_UP"),
    ];

    internal static RfcRequest BuildCostSheetRequest(CostSheetRequest body)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", ";")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZCOST_INFO3" })
            .TableRow("QUERY_TABLES", new { TABNAME = "PATN" })
            .TableRow("QUERY_TABLES", new { TABNAME = "ZCOST_SHEET" });

        foreach (var (table, field) in CostSheetFields)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "ZCOST_INFO3", FLD_FROM = "PATNR", TAB_TO = "PATN",        FLD_TO = "PATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "PATN",        FLD_FROM = "WERK",  TAB_TO = "ZCOST_SHEET", FLD_TO = "WERKS" });

        var year = DateTime.ParseExact(body.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture).Year;
        builder
            .WhereCondition($"ZCOST_INFO3~WERKS EQ '{Plant}'")
            .WhereCondition($"ZCOST_INFO3~FEH_STA EQ '{StatusFilter}'")
            .WhereCondition("ZCOST_INFO3~BUKRS EQ '0312'")
            .WhereCondition($"ZCOST_INFO3~BIDAT EQ '{body.Date}'")
            .WhereCondition($"ZCOST_SHEET~VALID_FROM EQ '01.01.{year}'");

        if (body.Materials.Count > 0)
        {
            builder.WhereCondition("ZCOST_INFO3~MATNR IN opt");

            foreach (var mat in body.Materials)
            {
                builder.TableItemRow("value_list", new
                {
                    TABNAME   = "ZCOST_INFO3",
                    FIELDNAME = "MATNR",
                    SIGN      = "I",
                    OPTION    = "",
                    LOW       = SapPad.Pad(mat, 18),
                    HIGH      = ""
                });
            }
        }

        builder.ReadTable("DATA_display");
        return builder.Build();
    }

    internal static CostSheetRow[] ParseCostSheetRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("DATA_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, ';', skipHeader: true)
            .Where(cols => cols.Length >= CostSheetFields.Length)
            .Select(cols => new CostSheetRow
            {
                Material       = cols[0],
                Plant          = cols[1],
                CostingDate    = cols[2],
                ValidTo        = cols[3],
                ProfitCenter   = cols[4],
                CompanyCode    = cols[5],
                PartnerNumber  = cols[6],
                Kst001         = Dec(cols[7]),
                Kst008         = Dec(cols[8]),
                Kst017         = Dec(cols[9]),
                Kst002         = Dec(cols[10]),
                Kst004         = Dec(cols[11]),
                Kst019         = Dec(cols[12]),
                Kst006         = Dec(cols[13]),
                Kst033         = Dec(cols[14]),
                LotSize        = Dec(cols[15]),
                Unit           = cols[16],
                Status         = cols[17],
                Work           = cols[18],
                SheetValidFrom = cols[19],
                SheetValidTo   = cols[20],
                OverheadPct    = Dec(cols[21]),
                IcMarkUp       = Dec(cols[22]),
            })
            .ToArray();
    }


    internal static RfcRequest BuildPeriodBalances(
        PeriodBalanceRequest body,
        string glAccount)
    {
        var builder = new RfcRequestBuilder(FnPeriodBalances)

            .Import("COMPANYCODE", CompanyCode)
            .Import("GLACCT", SapPad.Pad(glAccount,10))
            .Import("FISCALYEAR", body.FiscalYear)
            .Import("CURRENCYTYPE", "10")

            .ReadTable("ACCOUNT_BALANCES",
                "COMP_CODE",
                "GL_ACCOUNT",
                "FISC_YEAR",
                "FIS_PERIOD",
                "DEBITS_PER",
                "CREDIT_PER",
                "BALANCE",
                "CURRENCY"
            )

            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }


    internal static PeriodBalanceRow[] ParsePeriodBalances(
        RfcResponse response,
        string periodFrom,
        string periodTo)
    {
        if (!response.Tables.TryGetValue("ACCOUNT_BALANCES", out var rows))
            return [];

        int from = int.Parse(periodFrom);
        int to   = int.Parse(periodTo);

        return rows
            .Select(r =>
            {
                var periodStr = r.GetString("FIS_PERIOD");

                return new PeriodBalanceRow
                {
                    GlAccount = r.GetString("GL_ACCOUNT"),
                    Period    = periodStr,
                    Debit     = r.GetDecimal("DEBITS_PER"),
                    Credit    = r.GetDecimal("CREDIT_PER"),
                    Balance   = r.GetDecimal("BALANCE"),
                    CumBalance = 0
                };
            })
            .Where(r =>
            {
                if (!int.TryParse(r.Period, out var p)) return false;
                return p >= from && p <= to;
            })
            .ToArray();
    }


    internal static RfcRequest BuildProfitCenterRequest(ProfitCenterRequest body)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "GLPCA" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "RACCT" }); // GL Account
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "RPRCTR" }); // Profit Center
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "RYEAR" }); // Year
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "BUDAT" }); // Posting Date format YYYYMMDD
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "HSL" }); // Value in Company Code Currency (GBP)
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "REFDOCNR" }); // Invoice Number
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "REFDOCLN" }); // Invoice Item
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "MATNR" }); // Material Number
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "KUNNR" }); // Customer
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "AUBEL" }); // Sales Order
        builder.TableItemRow("query_FIELDS", new { TABNAME = "GLPCA", FIELDNAME = "AUPOS" }); // Sales Order Item

        var dateFrom = DateTime.ParseExact(body.DateFrom, "dd.MM.yyyy", CultureInfo.InvariantCulture);
        var dateTo   = DateTime.ParseExact(body.DateTo,   "dd.MM.yyyy", CultureInfo.InvariantCulture);

        var periodFrom = dateFrom.Month.ToString("D2");
        var periodTo   = dateTo.Month.ToString("D2");

        var YearFrom = dateFrom.Year.ToString();
        var YearTo   = dateTo.Year.ToString();

        builder
            .WhereCondition($"GLPCA~POPER BETWEEN '{periodFrom}' AND '{periodTo}'")
            .WhereCondition($"GLPCA~RYEAR BETWEEN '{YearFrom}' AND '{YearTo}'")
            .WhereCondition($"GLPCA~RRCTY EQ '0'")
            .WhereCondition($"GLPCA~RBUKRS EQ '0312'")
            .WhereCondition($"GLPCA~RACCT IN opt");
        
        foreach (var acct in body.GlAccounts)
        {
            builder.TableItemRow("value_list", new
            {
                TABNAME   = "GLPCA",
                FIELDNAME = "RACCT",
                SIGN      = "I",
                OPTION    = "",
                LOW       = SapPad.Pad(acct, 10),
                HIGH      = ""
            });
        }

        builder.ReadTable("DATA_display");
        return builder.Build();
    }


internal static ProfitCenterRow[] ParseProfitCenterRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("DATA_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 5)
            .Select(cols => new ProfitCenterRow
            {
                GlAccount   = cols[0],
                ProfitCenter = cols[1],
                FiscalYear = cols[2],
                PostingDate = cols[3],
                CompanyCodeValue = Dec(cols[4]),
                InvoiceNumber = cols[5],
                InvoiceItem = cols[6],
                MaterialNumber = cols[7],
                Customer = cols[8],
                SalesOrder = cols[9],
                SalesOrderItem = cols[10]
            })
            .ToArray();
    }



    private static decimal Dec(string s) =>
        decimal.TryParse(
            s.Replace(".", "").Replace(',', '.'),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d)
            ? d
            : 0m;




    internal static RfcRequest BuildFreightPostingRequest(
        FreightPostingRequest body, string? paymentTerms)
    {
        var pstDate = DateTime.Now.ToString("yyyyMMdd");

        return new RfcRequestBuilder("BAPI_ACC_DOCUMENT_POST")

            // ========================
            // DOCUMENT HEADER
            // ========================
            .StructImport("DOCUMENTHEADER", new
            {
                USERNAME   = "RFC_NEXUS",
                COMP_CODE  = CompanyCode,
                DOC_DATE   = body.DocDate,
                PSTNG_DATE = pstDate,
                DOC_TYPE   = "KR",
                HEADER_TXT = body.Shipment,
                REF_DOC_NO = body.Shipment,
                BUS_ACT    = "RFBU"
            })


            // ========================
            // ACCOUNTS PAYABLE (Vendor)
            // ========================
            .TableRow("ACCOUNTPAYABLE", new
            {
                ITEMNO_ACC = "0000000001",
                VENDOR_NO  = SapPad.Pad(body.Vendor,10),
                COMP_CODE  = CompanyCode,
                ITEM_TEXT  = body.Information,
                PROFIT_CTR = SapPad.Pad(body.ProfitCenter, 10),
                PMNTTRMS   = !string.IsNullOrEmpty(paymentTerms) ? paymentTerms : "ZB30" // Default to 30 days if not provided
            })

            // ========================
            // GL ACCOUNT (Freight Cost)
            // ========================
            .TableRow("ACCOUNTGL", new
            {
                ITEMNO_ACC = "0000000002",
                GL_ACCOUNT = SapPad.Pad(body.GlAccount, 10),
                PROFIT_CTR = SapPad.Pad(body.ProfitCenter, 10),
                COSTCENTER = SapPad.Pad(body.ProfitCenter, 10),
                COMP_CODE  = CompanyCode,
                PSTNG_DATE = pstDate,
                ITEM_TEXT = body.Information
            })

            // ========================
            // CURRENCY AMOUNTS
            // ========================
            .TableRow("CURRENCYAMOUNT", new
            {
                ITEMNO_ACC = "0000000001",
                CURRENCY   = body.Currency,
                AMT_DOCCUR = body.Amount * -1   // Vendor = DEBIT
            })
            .TableRow("CURRENCYAMOUNT", new
            {
                ITEMNO_ACC = "0000000002",
                CURRENCY   = body.Currency,
                AMT_DOCCUR = body.Amount        // GL = CREDIT
            })

            // ========================
            // RETURN MESSAGES
            // ========================
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .ReadParam("OBJ_KEY")   // Contains BELNR+GJAHR

            .Build();

    }



    internal static FreightPostingRow ParseFreightPostingRows(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");
        var tempKey = ReturnTableHelper.GetParam(response, "OBJ_KEY") ?? "";
        var accountingNumber = "";
        
        if (!string.IsNullOrWhiteSpace(tempKey) && tempKey.Length >= 14)
        {
            accountingNumber = tempKey.Substring(0, 10);
            var gjahr = tempKey.Substring(10, 4);
        }

        return new FreightPostingRow
        {
            AccountingNumber    = accountingNumber,
            Success             = true,
            Messages            = messages
                .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
                .ToList()
        };
    }



}
