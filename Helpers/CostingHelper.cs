using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class CostingHelper
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";
    internal const string Plant        = "3012";
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
                    OPTION    = "EQ",
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

    private static decimal Dec(string s) =>
        decimal.TryParse(
            s.Replace(".", "").Replace(',', '.'),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d)
            ? d
            : 0m;
}
