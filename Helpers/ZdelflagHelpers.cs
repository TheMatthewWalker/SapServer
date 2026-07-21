using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

// ── Request/response models ──────────────────────────────────────────────────
//
// Backs the ZDELFLAG/ZDELPACK maintenance step that fires once a delivery is
// marked complete in the pallet builder (see WarehouseController's
// "zdelflag/*" endpoints and DeliveryMain's PATCH /:deliveryId/complete on
// the Node side, which orchestrates these small lookups before assembling
// and posting the final T_DELFLAG/T_DELPACK rows). Ported from the uploaded
// zpil9_code.bas macro's maint_delflag_pack/ZDELFLAG_add_row/ZDELpack_add_row
// (the real production entry point — NOT upd_zdelflag, which is a dead/test
// path that leaves most header fields blank), simplified to this app's own
// PalletMain/PalletPackages data model per the user's explicit answers:
//   - One T_DELFLAG row per PalletPackages row (VHART "SMBX"), plus one
//     combined header row per PalletMain (VHART "PALL").
//   - PACKID = PalletMain.palletID * 1000 for the header row, then +1, +2...
//     per package row on that pallet.
//   - PALLET = "G" always, except "S" when the pallet has no palletType set
//     (palletType IS NULL/blank — there is no "None" row in PalletData; a
//     pallet built without picking a type is the only way this happens).
//   - Weights (NTGEW/BRGEW) only populated on the header row, from that
//     pallet's own gross/net weight (mirrors the ZDEL rollup); package rows
//     default to 0.
//   - BOXES = the pallet's actual package count on the header row, "1" on
//     each package row.
//   - T_DELPACK: one row per (package, ZBOM_INFO~IDNRK) pair, keyed to that
//     package's own PACKID, MENGE always 1, MEINS "EA", GEWEI "KG", TAREWEI
//     from PackagingData.packWeight (0 if not found).
//   - LGORT/MEINS/VOLUM/VOLEH/MTART on T_DELFLAG are left blank — the real
//     production macro (maint_delflag_pack) never sets these either; only
//     the dead upd_zdelflag/run_Z_MAINT_ZDELFLAG_ZDELPACK paths do. DONE is
//     the one exception: the user explicitly asked for "X" there.
//   - P_PALL/P_SMBX are per-row print flags (not top-level export params —
//     confirmed against ZDELFLAG_add_row's optional pall_print/box_print
//     args). Pallet-label printing is wired in now (header rows, always
//     true). Box-label printing is deliberately left available but unused
//     here — the user wants that triggered separately at batch-scan time,
//     a future feature — so package rows always send PrintBoxLabel=false
//     from Node today.

public sealed record ZdelflagLipsItemRow(string ItemNumber, string Description, string CustomerMaterial, string SalesUnit);

public sealed record ZbomInfoRequest
{
    public List<string> PackagingInstructions { get; init; } = [];
}

public sealed record ZbomInfoRow(string PackagingInstruction, string ComponentMaterial);

public sealed record DelflagRowRequest
{
    public string  Vbeln            { get; init; } = "";
    public string  Posnr            { get; init; } = ""; // blank on the header row
    public string  Charg            { get; init; } = ""; // blank on the header row
    public string  Kunnr            { get; init; } = "";
    public string  Empst            { get; init; } = "";
    public string  Werks            { get; init; } = "3012";
    public decimal Ntgew            { get; init; }        // header row only, else 0
    public decimal Brgew            { get; init; }        // header row only, else 0
    public string  Kdmat            { get; init; } = ""; // blank on the header row
    public decimal Lfimg            { get; init; }        // 0 on the header row
    public string  Eikto            { get; init; } = "";
    public string  Arktx            { get; init; } = ""; // blank on the header row
    public string  Matnr            { get; init; } = ""; // blank on the header row
    public string  Budat            { get; init; } = ""; // yyyyMMdd, posting date
    public string  Packid           { get; init; } = "";
    public string  Boxes            { get; init; } = "";
    public string  Pallet           { get; init; } = ""; // "G" / "S"
    public string  Vhart            { get; init; } = ""; // "PALL" / "SMBX"
    public string  SmbxMatnr        { get; init; } = "";
    public string  PallMatnr        { get; init; } = "";
    public string  Mtart            { get; init; } = ""; // always blank
    public string  Smbxhu           { get; init; } = "";
    public string  Done             { get; init; } = "X";
    public bool    PrintPalletLabel { get; init; }
    public bool    PrintBoxLabel    { get; init; }
}

public sealed record DelpackRowRequest
{
    public string  Packid    { get; init; } = "";
    public string  PallMatnr { get; init; } = "";
    public decimal Menge     { get; init; } = 1;
    public string  Meins     { get; init; } = "EA";
    public decimal Tarewei   { get; init; }
    public string  Gewei     { get; init; } = "KG";
}

public sealed record MaintainZdelflagRequest
{
    public List<DelflagRowRequest> DelflagRows { get; init; } = [];
    public List<DelpackRowRequest> DelpackRows { get; init; } = [];
}

public sealed record MaintainZdelflagResponse(string Rc, List<SapReturnMessage> Messages);

// ── Helpers ───────────────────────────────────────────────────────────────────

internal static class ZdelflagHelpers
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string FnMaintain   = "Z_MAINT_ZDELFLAG_ZDELPACK";
    private const string Plant        = "3012"; // reused as VKORG, same convention as CustomsHelpers' KNVV lookup

    // ── LIKP~ABLAD (EMPST) ───────────────────────────────────────────────────

    internal static RfcRequest BuildLikpAbladRequest(string delivery)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LIKP" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LIKP", FIELDNAME = "VBELN" })
            .TableItemRow("query_FIELDS", new { TABNAME = "LIKP", FIELDNAME = "ABLAD" });

        builder.WhereCondition($"LIKP~VBELN EQ '{SapPad.Pad(delivery, 10)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static string ParseLikpAblad(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return "";

        var row = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        return row is { Length: >= 2 } ? row[1].Trim() : "";
    }

    // ── LIPS (ARKTX, KDMAT, VRKME) per item ─────────────────────────────────
    //
    // ARKTX = short text (used as ZDELFLAG's "alert", the part's plain name),
    // KDMAT = customer material, VRKME = sales unit — all per delivery item
    // (POSNR), since a delivery can have several line items.

    private static readonly string[] LipsItemColumns = ["POSNR", "ARKTX", "KDMAT", "VRKME"];

    internal static RfcRequest BuildLipsItemDetailRequest(string delivery)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LIPS" });

        foreach (var f in LipsItemColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LIPS", FIELDNAME = f });

        builder.WhereCondition($"LIPS~VBELN EQ '{SapPad.Pad(delivery, 10)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static ZdelflagLipsItemRow[] ParseLipsItemDetail(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= LipsItemColumns.Length)
            .Select(cols => new ZdelflagLipsItemRow(cols[0], cols[1], cols[2], cols[3]))
            .ToArray();
    }

    // ── KNVV~EIKTO ───────────────────────────────────────────────────────────
    //
    // Keyed on KUNNR + VKORG (sales org, reused Plant constant "3012" per the
    // user's explicit instruction — same convention CustomsHelpers.BuildKnvvRequest
    // already uses for INCO1).

    internal static RfcRequest BuildKnvvEiktoRequest(string customer)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "KNVV" })
            .TableItemRow("query_FIELDS", new { TABNAME = "KNVV", FIELDNAME = "EIKTO" });

        builder
            .WhereCondition($"KNVV~VKORG EQ '{Plant}'")
            .WhereCondition($"KNVV~KUNNR EQ '{SapPad.Pad(customer, 10)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static string ParseKnvvEikto(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return "";

        var row = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        return row is { Length: >= 1 } ? row[0].Trim() : "";
    }

    // ── ZBOM_INFO~IDNRK (packaging instruction → component materials) ───────
    //
    // MATNR on ZBOM_INFO holds the packaging instruction string itself (e.g.
    // "IB_363660_MB", ZPRODBATCH~PALL_MATNR) — not a real material number —
    // so it's used as-is, not SapPad-padded to 18. A single instruction can
    // return several IDNRK rows; each becomes its own T_DELPACK row (see the
    // user's answer: "For packaging materials, should always be 1, otherwise
    // another row in T_DELPACK" — i.e. never combine multiple IDNRKs into one
    // higher-quantity row).

    private static readonly string[] ZbomInfoColumns = ["MATNR", "IDNRK"];

    internal static RfcRequest BuildZbomInfoRequest(List<string> packagingInstructions)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZBOM_INFO" });

        foreach (var f in ZbomInfoColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZBOM_INFO", FIELDNAME = f });

        builder.WhereCondition("ZBOM_INFO~MATNR IN opt");

        foreach (var instr in packagingInstructions.Distinct())
            builder.TableItemRow("value_list", new
            {
                TABNAME = "ZBOM_INFO", FIELDNAME = "MATNR",
                SIGN = "I", OPTION = "EQ", LOW = instr, HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static ZbomInfoRow[] ParseZbomInfoRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= ZbomInfoColumns.Length)
            .Select(cols => new ZbomInfoRow(cols[0].Trim(), cols[1].Trim()))
            .ToArray();
    }

    // ── Z_MAINT_ZDELFLAG_ZDELPACK ────────────────────────────────────────────
    //
    // The genuine RFC/BAPI itself — no BDC involved. Node has already done
    // every lookup and assembled the final rows by this point; this just
    // posts them as T_DELFLAG/T_DELPACK table rows and reads back RC +
    // ET_MESSAGE, exactly mirroring maint_delflag_pack's Z_MAINT_ZDELFLAG_ZDELPACK.Call.

    internal static RfcRequest BuildMaintainRequest(MaintainZdelflagRequest body)
    {
        var builder = new RfcRequestBuilder(FnMaintain);

        foreach (var r in body.DelflagRows)
            builder.TableRow("T_DELFLAG", new
            {
                VBELN      = SapPad.Pad(r.Vbeln, 10),
                POSNR      = r.Posnr,
                CHARG      = r.Charg,
                KUNNR      = SapPad.Pad(r.Kunnr, 10),
                EMPST      = r.Empst,
                WERKS      = r.Werks,
                NTGEW      = r.Ntgew,
                BRGEW      = r.Brgew,
                KDMAT      = r.Kdmat,
                LFIMG      = r.Lfimg,
                EIKTO      = r.Eikto,
                ARKTX      = r.Arktx,
                MATNR      = string.IsNullOrWhiteSpace(r.Matnr) ? "" : SapPad.Pad(r.Matnr, 18),
                BUDAT      = r.Budat,
                PACKID     = r.Packid,
                BOXES      = r.Boxes,
                PALLET     = r.Pallet,
                VHART      = r.Vhart,
                DONE       = r.Done,
                // SMBX_MATNR holds the packaging instruction string (e.g.
                // "IB_363660_MB", ZPRODBATCH~PALL_MATNR) — the exact same kind
                // of value as ZBOM_INFO~MATNR above, which is deliberately left
                // un-padded since it's a lookup key, not a real material
                // number. SapPad.Pad happens to no-op for mixed alphanumeric
                // strings like the example, but would incorrectly zero-pad a
                // purely numeric instruction value, breaking any SAP-side
                // match against ZBOM_INFO~MATNR — trim only, never pad.
                SMBX_MATNR = (r.SmbxMatnr ?? "").Trim(),
                PALL_MATNR = string.IsNullOrWhiteSpace(r.PallMatnr) ? "" : SapPad.Pad(r.PallMatnr, 18),
                MTART      = r.Mtart,
                SMBXHU     = r.Smbxhu,
                P_PALL     = r.PrintPalletLabel ? "X" : "",
                P_SMBX     = r.PrintBoxLabel    ? "X" : "",
            });

        foreach (var p in body.DelpackRows)
            builder.TableRow("T_DELPACK", new
            {
                PACKID     = p.Packid,
                PALL_MATNR = SapPad.Pad(p.PallMatnr, 18),
                MENGE      = p.Menge,
                MEINS      = p.Meins,
                TAREWEI    = p.Tarewei,
                GEWEI      = p.Gewei,
            });

        return builder
            .ReadParam("RC")
            .ReadTable("ET_MESSAGE", "TYPE", "MESSAGE")
            .Build();
    }

    internal static MaintainZdelflagResponse ParseMaintainResponse(RfcResponse response)
    {
        var rc       = ReturnTableHelper.GetParam(response, "RC") ?? "";
        var messages = ReturnTableHelper.ExtractMessages(response, "ET_MESSAGE")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        return new MaintainZdelflagResponse(rc, messages);
    }
}
