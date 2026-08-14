using System.Globalization;
using SapServer.Models;

namespace SapServer.Helpers;

// ── Request models ────────────────────────────────────────────────────────────

// Mirrors sd_waterfall.xltm's "dialog" sheet filter form exactly (recovered from
// the workbook's exported VBA module get_data.Get_vbak_vbap_vblb_vbeh /
// Get_vbak_vbap_vblb_vbep — see SapServer/CLAUDE.md-adjacent plan notes for the
// full reverse-engineering trail). ShipToParties/Materials map to the
// sold_to/matnr multi-cell ranges; IncludeForecast/IncludeJit map to the
// Forecast/JIT tick-box cells (VBLB~ABART '1'/'2'); IdocCreatedAfter maps to
// idoc_date (VBLB~ERDAT GT); ScheduleDateFrom/To map to edatu_from/edatu_to.
public sealed record ScheduleWaterfallRequest
{
    public string SalesOrg { get; init; } = "";
    public List<string> ShipToParties { get; init; } = [];
    public List<string> Materials { get; init; } = [];
    public bool IncludeForecast { get; init; } = true;
    public bool IncludeJit { get; init; } = true;
    public DateOnly? IdocCreatedAfter { get; init; }
    public DateOnly ScheduleDateFrom { get; init; }
    public DateOnly ScheduleDateTo { get; init; }

    // Source tool's "Show Idocs with 0 Qty." checkbox — drives an outer join
    // (vs. the default inner join) on the history/current table in the VBA
    // source. NOT wired into the RFC request yet: the VBA addresses the
    // outer-join flag positionally on the QUERY_TABLES row (`qt(4, 2) = "X"`)
    // and the real field name behind that second column isn't recoverable
    // without the SAP-side ZRFC_READ_TABLES function signature (SE37) — every
    // other caller of this Z-RFC in this codebase (WarehouseHelpers,
    // CustomsHelpers, PerformanceHelpers) only ever sets TABNAME. Until
    // confirmed against the real system, requests always run the inner-join
    // path, which is also the tool's own default (unchecked) state.
    public bool IncludeZeroQty { get; init; }
}

// ── Response models ───────────────────────────────────────────────────────────

// One row per (release, schedule line) — either a past pull (from VBEH,
// IsCurrent = false) or the live/current schedule (from VBEP, IsCurrent =
// true), unioned exactly like the source tool unions Get_vbak_vbap_vblb_vbeh's
// Data-sheet rows with Get_vbak_vbap_vblb_vbep's appended rows.
public sealed record ScheduleWaterfallRow
{
    public string ShipToParty { get; init; } = "";        // VBAK~KUNNR
    public string SalesDocument { get; init; } = "";       // VBAP~VBELN
    public string SalesDocItem { get; init; } = "";        // VBAP~POSNR
    public string Material { get; init; } = "";            // VBAP~MATNR
    public string MaterialDescription { get; init; } = ""; // VBAP~ARKTX
    public string IdocNumber { get; init; } = "";           // VBLB~DOCNUM
    public decimal CumQty { get; init; }                    // VBLB~ABEFZ — SAP's own cumulative-to-date figure
    public DateOnly? IdocCreationDate { get; init; }        // VBLB~ERDAT
    public string EntryTime { get; init; } = "";            // VBLB~ERZEI
    public DateOnly? ScheduleLineDate { get; init; }        // VBEH~EDATU / VBEP~EDATU
    public decimal OrderQty { get; init; }                  // VBEH~WMENG / VBEP~WMENG
    public string SalesOrg { get; init; } = "";             // VBAK~VKORG
    public string ReleaseType { get; init; } = "";          // VBLB~ABART ("1" Forecast, "2" JIT)
    public string LastDel { get; init; } = "";               // VBLB~LFNKD
    public int ScheduleWeek { get; init; }                  // derived: ISO year*100 + week of ScheduleLineDate
    public int IdocWeek { get; init; }                      // derived: ISO year*100 + week of IdocCreationDate
    public bool IsCurrent { get; init; }                    // true for VBEP (live schedule) rows, false for VBEH (history) rows

    // Client-computed running total of OrderQty across ScheduleWeek, within
    // each (ShipToParty, Material, IdocNumber-or-"current") group, ordered by
    // ScheduleLineDate ascending — reconstructing sd_waterfall.xltm's
    // "Cum. rel" column (added by a CumRel_add macro in a VBA module that
    // wasn't part of the exported get_data.bas, so its exact formula isn't
    // recoverable). SAP's own CumQty (VBLB~ABEFZ) above is the authoritative
    // cumulative figure where the two disagree — cross-check this against the
    // legacy Excel tool with identical filters before relying on it.
    public decimal CumulativeRelease { get; init; }
}

internal static class SalesHelpers
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";

    // Column order matches the add_qf(...) call order in get_data.bas exactly
    // (both Get_vbak_vbap_vblb_vbeh and Get_vbak_vbap_vblb_vbep register
    // fields in this same order) — ZRFC_READ_TABLES' data_display WA column
    // is delimited positionally, so this order is load-bearing.
    private static readonly (string Table, string Field)[] CommonFields =
    [
        ("VBAK", "KUNNR"),
        ("VBAP", "VBELN"),
        ("VBAP", "POSNR"),
        ("VBAP", "MATNR"),
        ("VBAP", "ARKTX"),
        ("VBLB", "DOCNUM"),
        ("VBLB", "ABEFZ"),
        ("VBLB", "ERDAT"),
        ("VBLB", "ERZEI"),
        // index 9/10 swapped in per-table field name only (EDATU/WMENG come
        // from VBEH for history, VBEP for current) — same column positions.
        ("VBAK", "VKORG"),
        ("VBLB", "ABART"),
        ("VBLB", "LFNKD"),
    ];

    // ── History (VBAK/VBAP/VBLB/VBEH) — past schedule pulls ─────────────────

    internal static RfcRequest BuildHistoryRequest(ScheduleWaterfallRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "VBAK" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBAP" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBLB" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBEH" });

        AddCommonFields(builder);
        builder
            .TableItemRow("query_FIELDS", new { TABNAME = "VBEH", FIELDNAME = "EDATU" })
            .TableItemRow("query_FIELDS", new { TABNAME = "VBEH", FIELDNAME = "WMENG" });

        AddCommonJoins(builder);
        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "MANDT", TAB_TO = "VBEH", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "VBELN", TAB_TO = "VBEH", FLD_TO = "VBELN" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "POSNR", TAB_TO = "VBEH", FLD_TO = "POSNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "ABRLI", TAB_TO = "VBEH", FLD_TO = "ABRLI" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "ABART", TAB_TO = "VBEH", FLD_TO = "ABART" });

        AddCommonWhere(builder, req);

        // "Can't have wc when outer join" per the VBA source — these two
        // filters are only safe to push to SAP while running the (current,
        // default) inner-join path; see IncludeZeroQty's doc comment.
        if (!req.IncludeZeroQty)
        {
            builder.WhereCondition("VBEH~EDATU IN opt");
            builder.TableItemRow("value_list", new
            {
                TABNAME = "VBEH",
                FIELDNAME = "EDATU",
                SIGN = "I",
                OPTION = "BT",
                LOW = SapDate(req.ScheduleDateFrom),
                HIGH = SapDate(req.ScheduleDateTo),
            });
            builder.WhereCondition("VBEH~WMENG > 0");
        }

        return builder.ReadTable("data_display").Build();
    }

    // ── Current (VBAK/VBAP/VBLB/VBEP) — live/not-yet-historized schedule ────

    internal static RfcRequest BuildCurrentRequest(ScheduleWaterfallRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA", " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "VBAK" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBAP" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBLB" })
            .TableRow("QUERY_TABLES", new { TABNAME = "VBEP" });

        AddCommonFields(builder);
        builder
            .TableItemRow("query_FIELDS", new { TABNAME = "VBEP", FIELDNAME = "EDATU" })
            .TableItemRow("query_FIELDS", new { TABNAME = "VBEP", FIELDNAME = "WMENG" });

        AddCommonJoins(builder);
        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "MANDT", TAB_TO = "VBEP", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "VBELN", TAB_TO = "VBEP", FLD_TO = "VBELN" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "POSNR", TAB_TO = "VBEP", FLD_TO = "POSNR" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBLB", FLD_FROM = "ABART", TAB_TO = "VBEP", FLD_TO = "ABART" });

        AddCommonWhere(builder, req);

        // VBLB~ABRLI EQ '0' — the "current" (not-yet-historized) release
        // line, per get_data.bas's Get_vbak_vbap_vblb_vbep.
        builder.WhereCondition("VBLB~ABRLI EQ '0'");

        builder.WhereCondition("VBEP~EDATU IN opt");
        builder.TableItemRow("value_list", new
        {
            TABNAME = "VBEP",
            FIELDNAME = "EDATU",
            SIGN = "I",
            OPTION = "BT",
            LOW = SapDate(req.ScheduleDateFrom),
            HIGH = SapDate(req.ScheduleDateTo),
        });
        builder.WhereCondition("VBEP~WMENG > 0");

        return builder.ReadTable("data_display").Build();
    }

    private static void AddCommonFields(RfcRequestBuilder builder)
    {
        foreach (var (table, field) in CommonFields)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });
    }

    private static void AddCommonJoins(RfcRequestBuilder builder)
    {
        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBAK", FLD_FROM = "MANDT", TAB_TO = "VBAP", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBAK", FLD_FROM = "VBELN", TAB_TO = "VBAP", FLD_TO = "VBELN" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBAP", FLD_FROM = "MANDT", TAB_TO = "VBLB", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBAP", FLD_FROM = "VBELN", TAB_TO = "VBLB", FLD_TO = "VBELN" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "VBAP", FLD_FROM = "POSNR", TAB_TO = "VBLB", FLD_TO = "POSNR" });
    }

    private static void AddCommonWhere(RfcRequestBuilder builder, ScheduleWaterfallRequest req)
    {
        builder.WhereCondition($"VBAK~VKORG EQ '{SapPad.Pad(req.SalesOrg, 4)}'");

        builder.WhereCondition("VBAK~KUNNR IN opt");
        foreach (var shipTo in req.ShipToParties)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "VBAK", FIELDNAME = "KUNNR",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(shipTo, 10), HIGH = ""
            });

        // Unlike the VBA source (which always writes an MATNR IN where-clause,
        // even with zero value_list rows — only safe there because the tool's
        // real-world usage apparently never left Material blank), an empty
        // Materials list here means "no material restriction" rather than
        // "match nothing".
        if (req.Materials.Count > 0)
        {
            builder.WhereCondition("VBAP~MATNR IN opt");
            foreach (var material in req.Materials)
                builder.TableItemRow("value_list", new
                {
                    TABNAME = "VBAP", FIELDNAME = "MATNR",
                    SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(material, 18), HIGH = ""
                });
        }

        builder.WhereCondition("VBLB~ABART IN opt");
        if (req.IncludeForecast)
            builder.TableItemRow("value_list", new { TABNAME = "VBLB", FIELDNAME = "ABART", SIGN = "I", OPTION = "EQ", LOW = "1", HIGH = "" });
        if (req.IncludeJit)
            builder.TableItemRow("value_list", new { TABNAME = "VBLB", FIELDNAME = "ABART", SIGN = "I", OPTION = "EQ", LOW = "2", HIGH = "" });

        if (req.IdocCreatedAfter is { } after)
            builder.WhereCondition($"VBLB~ERDAT GT '{SapDate(after)}'");
    }

    private static string SapDate(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // ── Response parsing ─────────────────────────────────────────────────────

    internal static ScheduleWaterfallRow[] ParseRows(RfcResponse response, bool isCurrent)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 11)
            .Select(cols =>
            {
                var idocCreationDate = ParseSapDate(cols[7]);
                var scheduleLineDate = ParseSapDate(cols[9]);

                return new ScheduleWaterfallRow
                {
                    ShipToParty          = cols[0],
                    SalesDocument        = cols[1],
                    SalesDocItem         = cols[2],
                    Material             = cols[3],
                    MaterialDescription  = cols[4],
                    IdocNumber           = cols[5],
                    CumQty               = ParseDecimal(cols[6]),
                    IdocCreationDate     = idocCreationDate,
                    EntryTime            = cols[8],
                    ScheduleLineDate     = scheduleLineDate,
                    OrderQty             = ParseDecimal(cols[10]),
                    SalesOrg             = cols.Length > 11 ? cols[11] : "",
                    ReleaseType          = cols.Length > 12 ? cols[12] : "",
                    LastDel              = cols.Length > 13 ? cols[13] : "",
                    ScheduleWeek         = WeekNumber(scheduleLineDate),
                    IdocWeek             = WeekNumber(idocCreationDate),
                    IsCurrent            = isCurrent,
                };
            })
            .ToArray();
    }

    private static decimal ParseDecimal(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    // SAP dates come back from ZRFC_READ_TABLES as "YYYYMMDD" (internal ABAP
    // date format) — "00000000" (or blank) means null/no value, matching
    // get_data.bas's own "00.00.0000" blank-date check.
    private static DateOnly? ParseSapDate(string value)
    {
        if (value.Length != 8 || value == "00000000") return null;
        return DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // ISO-8601 week numbering (year*100 + week), equivalent to get_data.bas's
    // own WEEKNR/WEEKNR_full functions — those implement the same
    // Thursday-of-the-week ISO rule by hand; ISOWeek does it natively.
    private static int WeekNumber(DateOnly? date)
    {
        if (date is not { } d) return 0;
        var dt = d.ToDateTime(TimeOnly.MinValue);
        return ISOWeek.GetYear(dt) * 100 + ISOWeek.GetWeekOfYear(dt);
    }

    // ── Cum. rel reconstruction ───────────────────────────────────────────
    //
    // See ScheduleWaterfallRow.CumulativeRelease's doc comment: this is a
    // best-effort reconstruction of the source tool's CumRel_add macro (not
    // in the exported get_data.bas module), not a verified port. Running
    // total of OrderQty, grouped by (ShipToParty, Material, release identity
    // — IdocNumber for history rows, "current" for VBEP rows), ordered by
    // ScheduleLineDate ascending.
    internal static ScheduleWaterfallRow[] ComputeCumulativeRelease(IEnumerable<ScheduleWaterfallRow> rows)
    {
        return rows
            .GroupBy(r => (r.ShipToParty, r.Material, Release: r.IsCurrent ? "current" : r.IdocNumber))
            .SelectMany(g =>
            {
                decimal running = 0m;
                return g
                    .OrderBy(r => r.ScheduleLineDate)
                    .Select(r =>
                    {
                        running += r.OrderQty;
                        return r with { CumulativeRelease = running };
                    });
            })
            .ToArray();
    }
}
