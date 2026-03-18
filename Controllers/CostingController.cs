using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;
using System.Globalization;
using System.Security.AccessControl;

namespace SapServer.Controllers;

[Route("api/costing")]
public sealed class CostingController : SapControllerBase
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string Plant        = "3012";
    private const string StatusFilter = "FR";

    // Column order must exactly match query_FIELDS registration order below
    private static readonly (string Table, string Field)[] CostSheetFields =
    [
        ("ZCOST_INFO3", "MATNR"),      // [0]
        ("ZCOST_INFO3", "WERKS"),      // [1]
        ("ZCOST_INFO3", "KADAT"),      // [2]
        ("ZCOST_INFO3", "BIDAT"),      // [3]
        ("ZCOST_INFO3", "PRCTR"),      // [4]
        ("ZCOST_INFO3", "BUKRS"),      // [5]
        ("ZCOST_INFO3", "PATNR"),      // [6]
        ("ZCOST_INFO3", "KST001"),     // [7]
        ("ZCOST_INFO3", "KST008"),     // [8]
        ("ZCOST_INFO3", "KST017"),     // [9]
        ("ZCOST_INFO3", "KST002"),     // [10]
        ("ZCOST_INFO3", "KST004"),     // [11]
        ("ZCOST_INFO3", "KST019"),     // [12]
        ("ZCOST_INFO3", "KST006"),     // [13]
        ("ZCOST_INFO3", "KST033"),     // [14]
        ("ZCOST_INFO3", "LOSGR"),      // [15]
        ("ZCOST_INFO3", "MEINS"),      // [16]
        ("ZCOST_INFO3", "FEH_STA"),    // [17]
        ("PATN",        "WERK"),       // [18]
        ("ZCOST_SHEET", "VALID_FROM"), // [19]
        ("ZCOST_SHEET", "VALID_TO"),   // [20]
        ("ZCOST_SHEET", "OH_PCT"),     // [21]
        ("ZCOST_SHEET", "IC_MARK_UP"), // [22]
    ];

    public CostingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<CostingController> logger)
        : base(pool, permissions, logger) { }

    // ── POST /api/costing/cost-sheet ─────────────────────────────────────────

    [HttpPost("cost-sheet")]
    [ProducesResponseType(typeof(ApiResponse<CostSheetRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetCostSheet(
        [FromBody] CostSheetRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnReadTables, ct);

        var request = BuildCostSheetRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var response = await _pool.ExecuteAsync(request, ct);
        return Ok(ApiResponse<CostSheetRow[]>.Ok(ParseCostSheetRows(response)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RfcRequest BuildCostSheetRequest(CostSheetRequest body)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", ";")
            .Import("NO_DATA",   " ")
            // Three tables in join order (InputTables → func.Tables("QUERY_TABLES"))
            .TableRow("QUERY_TABLES", new { TABNAME = "ZCOST_INFO3" })
            .TableRow("QUERY_TABLES", new { TABNAME = "PATN" })
            .TableRow("QUERY_TABLES", new { TABNAME = "ZCOST_SHEET" });

        // query_FIELDS — 23 fields in column order (InputTablesItems → func.Tables.Item())
        foreach (var (table, field) in CostSheetFields)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });

        // join_FIELDS — two joins using TAB_FROM/FLD_FROM/TAB_TO/FLD_TO
        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "ZCOST_INFO3", FLD_FROM = "PATNR", TAB_TO = "PATN",        FLD_TO = "PATNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "PATN",        FLD_FROM = "WERK",  TAB_TO = "ZCOST_SHEET", FLD_TO = "WERKS" });

        // WHERE clause — pass date strings through directly, no reformatting
        // body.Date is expected in dd.MM.yyyy format, matching SAP's date format
        var year = DateTime.ParseExact(body.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture).Year;
        builder
            .WhereCondition($"ZCOST_INFO3~WERKS EQ '3012'")
            .WhereCondition($"ZCOST_INFO3~FEH_STA EQ 'FR'")
            .WhereCondition($"ZCOST_INFO3~BUKRS EQ '0312'")
            .WhereCondition($"ZCOST_INFO3~BIDAT EQ '{body.Date}'")
            .WhereCondition($"ZCOST_SHEET~VALID_FROM EQ '01.01.{year}'");

        // Material filter via IN option list
        if (body.Materials.Count > 0)
        {
            builder.WhereCondition("ZCOST_INFO3~MATNR IN opt");

            foreach (var mat in body.Materials)
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

        builder.ReadTable("DATA_display");
        return builder.Build();
    }

    private static CostSheetRow[] ParseCostSheetRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("DATA_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, ';', skipHeader: true)
            .Where(cols => cols.Length >= CostSheetFields.Length)
            .Select(cols => new CostSheetRow
            {
                Material      = cols[0],
                Plant         = cols[1],
                CostingDate   = cols[2],
                ValidTo       = cols[3],
                ProfitCenter  = cols[4],
                CompanyCode   = cols[5],
                PartnerNumber = cols[6],
                Kst001        = Dec(cols[7]),
                Kst008        = Dec(cols[8]),
                Kst017        = Dec(cols[9]),
                Kst002        = Dec(cols[10]),
                Kst004        = Dec(cols[11]),
                Kst019        = Dec(cols[12]),
                Kst006        = Dec(cols[13]),
                Kst033        = Dec(cols[14]),
                LotSize       = Dec(cols[15]),
                Unit          = cols[16],
                Status        = cols[17],
                Work          = cols[18],
                SheetValidFrom = cols[19],
                SheetValidTo   = cols[20],
                OverheadPct   = Dec(cols[21]),
                IcMarkUp      = Dec(cols[22]),
            })
            .ToArray();
    }

    private static decimal Dec(string s)
        // SAP European format: '.' = thousands separator, ',' = decimal point
        // e.g. "1.234,56" → strip dots → "1234,56" → swap comma → "1234.56"
        => decimal.TryParse(s.Replace(".", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}
