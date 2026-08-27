using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// SAP calls for Engineering's "Packaging Data" tools (Mass Packaging Update,
/// New Packaging Creation, Packaging Instruction Detail) — ported from the
/// "363_new_packaging.xlsm" Excel/VBA tool the Engineering department
/// previously used directly against SAP.
///
/// Read lookups go through the generic ZRFC_READ_TABLES wrapper (same
/// pattern as every other lookup in this project — see ProductionHelpers.cs,
/// ZdelflagHelpers.cs). Writes use two different real named RFCs:
///   - Z_RFC_CALL_TRANSACTION (via BdcBuilder) for MM01 (create material)
///     and CS01 (create BOM) — direct port of the VBA's screene/field BDC
///     recording in new_packaging_code.bas.
///   - ZPACK_INSTR_MAINT — a genuine custom RFC (table IT_ZPACK_INSTR,
///     exports SQL_ACTION/TEST, import RC, table IT_MESSAGES) that inserts/
///     updates/deletes a packaging-instruction assignment row. Ported from
///     sap_bapi_code.bas's upd_pack_instr/upd_pack_instr_mass.
/// </summary>
internal static class PackagingHelpers
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";
    internal const string FnCreate     = "Z_RFC_CALL_TRANSACTION"; // permission-gate constant, mirrors ProductionHelpers.FnCreate
    internal const string FnPackMaint  = "ZPACK_INSTR_MAINT";
    internal const string Plant        = "3012";

    // Packaging-type code -> physical drum/box/carton component material used
    // as the single BOM component when creating a new packaging instruction.
    // Direct port of new_packaging_code.bas's get_drum().
    private static readonly Dictionary<string, string> DrumComponent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SD"] = "P_DRUMSML_NMT",
        ["MD"] = "P_DRUMMED_NMT",
        ["LD"] = "P_DRUMLGE_NMT",
        ["XD"] = "P_DRUMXLG_NMT",
        ["SB"] = "P_BOXPALLETSML_NMT",
        ["MB"] = "P_BOXPALLETMED_NMT",
        ["LB"] = "P_BOXPALLETLGE_NMT",
        ["XB"] = "P_BOXXL_NMT",
        ["C1"] = "P_CARTON1_NMT",
        ["C2"] = "P_CARTON2_NMT",
    };

    // The 10 packaging-type codes New Packaging Creation offers, in the same
    // order as the Excel's "new_packaging" tab column headers.
    internal static readonly string[] PackagingCodes = ["SD", "MD", "LD", "XD", "SB", "MB", "LB", "XB", "C1", "C2"];

    internal static bool TryGetDrumComponent(string code, out string component)
        => DrumComponent.TryGetValue(code, out component!);

    // Reference material every new packaging material is created as a copy
    // of (MM01's "copy from" material) — one per packaging-type code. Kept
    // hardcoded per the user's explicit instruction (matches the original
    // macro's "IB_363800_" & code exactly), not configurable.
    internal static string ReferenceMaterial(string code) => $"IB_363800_{code}";

    // The material this feature creates/assigns for a given customer part
    // number + packaging-type code — same "IB_<part>_<code>" convention
    // already used elsewhere in this app (e.g. Drumming's backflush, see
    // ProductionHelpers.BuildZf40nRequest in Normanton-Nexus... err SapServer
    // itself, ZF40N screen field ST_ZMARA_C_T-MATNR).
    internal static string PackagingMaterial(string customerPart, string code) => $"IB_{customerPart}_{code}";


// ── Reads ─────────────────────────────────────────────────────────────────

    // MARC existence check — does this material already exist at plant 3012?
    internal static RfcRequest BuildMaterialExistsRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "WERKS" });

        builder
            .WhereCondition($"MARC~MATNR EQ '{SapPad.Pad(material, 18)}'")
            .WhereCondition($"MARC~WERKS EQ '{Plant}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static bool ParseMaterialExists(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return false;
        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).Any();
    }

    // MAKT — material description (English).
    internal static RfcRequest BuildMaktRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MAKT" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MAKT", FIELDNAME = "MAKTX" });

        builder
            .WhereCondition($"MAKT~MATNR EQ '{SapPad.Pad(material, 18)}'")
            .WhereCondition("MAKT~SPRAS EQ 'E'");

        return builder.ReadTable("data_display").Build();
    }

    internal static string ParseSingleValue(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return "";
        var row = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        return row is { Length: >= 1 } ? row[0].Trim() : "";
    }

    // MARA — weight, material type, volume/handling unit type, weight unit.
    internal static RfcRequest BuildMaraRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" });

        foreach (var field in new[] { "BRGEW", "MTART", "VHART", "GEWEI" })
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = field });

        builder.WhereCondition($"MARA~MATNR EQ '{SapPad.Pad(material, 18)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static PackagingMaraRow? ParseMara(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return null;
        var cols = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        if (cols is not { Length: >= 4 }) return null;
        return new PackagingMaraRow
        {
            // No /1000 here -- same bug class as ParseZpackInstr's (see its
            // comment). Confirmed live via a raw ZRFC_READ_TABLES bypass:
            // CP104's real MARA-BRGEW is "0,021" (SAP's native comma-decimal
            // format, no thousands grouping) = 0.021 kg, a plausible real
            // component weight -- the extra /1000 made this display as an
            // implausible 0.000021 kg. ParseSapDecimal alone is correct.
            WeightKg     = RfcRowExtensions.ParseSapDecimal(cols[0]) ?? 0m,
            MaterialType = cols[1],
            HandlingType = cols[2],
            WeightUnit   = cols[3],
        };
    }

    // ZPACK_INSTR — the packaging-instruction assignment for a material, at
    // either customer level (kunnr set) or plant-default level (kunnr blank).
    internal static RfcRequest BuildZpackInstrRequest(string material, string customer)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZPACK_INSTR" });

        foreach (var field in new[] { "PALL_MATNR", "PALL_QTY", "SMBX_QTY", "PACK_PROD", "BOX_GEN", "BATCH_SPREAD", "PART_MIX", "CHARGE_REQ", "TECHSTAT_REQ", "PNUM_REQ" })
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZPACK_INSTR", FIELDNAME = field });

        builder.WhereCondition($"ZPACK_INSTR~MATNR EQ '{SapPad.Pad(material, 18)}'");
        builder.WhereCondition($"ZPACK_INSTR~WERKS EQ '{Plant}'");
        builder.WhereCondition(string.IsNullOrWhiteSpace(customer)
            ? "ZPACK_INSTR~KUNNR EQ ''"
            : $"ZPACK_INSTR~KUNNR EQ '{SapPad.Pad(customer, 10)}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static PackagingInstrRow? ParseZpackInstr(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return null;
        var c = SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true).FirstOrDefault();
        if (c is not { Length: >= 10 }) return null;
        return new PackagingInstrRow
        {
            PackMaterial = c[0].Trim(),
            // Confirmed live (2026-08-27): the extra /1000 here was itself the
            // bug, not a real gram->kg-style conversion. ParseSapDecimal
            // already correctly parses SAP's native comma-decimal quantity
            // text ("300,000" means 300, per the same convention documented
            // for RfcRowExtensions.ParseSapDecimal/GetDecimal) -- dividing
            // that result by 1000 again silently shrank the true value
            // 1000x. This is what actually caused CP104's real SmallBoxQty
            // corruption during endpoint testing: the (pre-existing, long-
            // standing) read-side bug made 300 display as 0.300, a mass-
            // update round-trip then wrote 0.3 straight back with no
            // conversion (the write side never had a bug), and the same
            // read bug then displayed THAT as 0.0003. The correct fix is
            // removing this division, not adding a matching multiplication
            // to the write side (BuildPackInstrMaintRequest) -- see its
            // comment. endpoint-test-log-2026-08-27.md, ROUND 2 #16 for the
            // full incident and this correction.
            PalletQty    = RfcRowExtensions.ParseSapDecimal(c[1]) ?? 0m,
            SmallBoxQty  = RfcRowExtensions.ParseSapDecimal(c[2]) ?? 0m,
            PackProd     = !string.IsNullOrWhiteSpace(c[3]),
            BoxGen       = !string.IsNullOrWhiteSpace(c[4]),
            BatchSpread  = !string.IsNullOrWhiteSpace(c[5]),
            PartMix      = !string.IsNullOrWhiteSpace(c[6]),
            ChargeReq    = !string.IsNullOrWhiteSpace(c[7]),
            TechStatReq  = !string.IsNullOrWhiteSpace(c[8]),
            PNumReq      = !string.IsNullOrWhiteSpace(c[9]),
        };
    }

    // ZBOM_INFO — packaging BOM components (drum/box/carton + any sub-parts)
    // for the material assigned as the packaging instruction.
    internal static RfcRequest BuildPackagingBomRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZBOM_INFO" });

        foreach (var field in new[] { "IDNRK", "MEINS", "MENGE" })
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZBOM_INFO", FIELDNAME = field });

        builder.WhereCondition($"ZBOM_INFO~MATNR EQ '{SapPad.Pad(material, 18)}'");
        builder.WhereCondition($"ZBOM_INFO~WERKS EQ '{Plant}'");

        return builder.ReadTable("data_display").Build();
    }

    internal static PackagingBomRow[] ParsePackagingBom(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];
        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true)
            .Where(c => c.Length >= 3)
            .Select(c => new PackagingBomRow
            {
                Component = c[0].Trim(),
                Unit      = c[1].Trim(),
                // No /1000 here -- same bug class as ParseZpackInstr/ParseMara
                // (see their comments). Confirmed live via a raw
                // ZRFC_READ_TABLES bypass: IB_CARTON2_NMT's real ZBOM_INFO-
                // MENGE is "1,000" (SAP's native comma-decimal, no thousands
                // grouping) = exactly 1 EA, a completely standard BOM
                // component quantity -- the extra /1000 made this display as
                // an implausible 0.001. ParseSapDecimal alone is correct.
                Quantity  = RfcRowExtensions.ParseSapDecimal(c[2]) ?? 0m,
            })
            .ToArray();
    }

    // ZKNA1_KNMT_V1 — customers this material is sold to, with their own
    // customer material number. LOEVM eq '' excludes deletion-flagged rows.
    internal static RfcRequest BuildCustomerListRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZKNA1_KNMT_V1" });

        foreach (var field in new[] { "KUNNR", "KONZS", "NAME1", "KDMAT", "VKORG" })
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZKNA1_KNMT_V1", FIELDNAME = field });

        builder.WhereCondition($"ZKNA1_KNMT_V1~MATNR EQ '{SapPad.Pad(material, 18)}'");
        builder.WhereCondition($"ZKNA1_KNMT_V1~WERKS EQ '{Plant}'");
        builder.WhereCondition("ZKNA1_KNMT_V1~LOEVM EQ ''");

        return builder.ReadTable("data_display").Build();
    }

    internal static PackagingCustomerRow[] ParseCustomerList(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];
        return SapDelimitedParser.ParseRows(sapRows, '|', skipHeader: true)
            .Where(c => c.Length >= 5)
            .Select(c => new PackagingCustomerRow
            {
                Customer     = c[0].Trim(),
                CustomerGroup = c[1].Trim(),
                Name         = c[2].Trim(),
                CustomerPart = c[3].Trim(),
                SalesOrg     = c[4].Trim(),
            })
            .ToArray();
    }


    // MARA-MBRSH (industry sector) for a single material -- used to read the
    // reference material's own industry sector before MM01 creation, since
    // BDC/call-transaction needs it supplied explicitly (see
    // BuildCreateMaterialRequest's header comment).
    internal static RfcRequest BuildIndustrySectorRequest(string material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" })
            .TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = "MBRSH" });

        builder.WhereCondition($"MARA~MATNR EQ '{SapPad.Pad(material, 18)}'");

        return builder.ReadTable("data_display").Build();
    }

// ── Writes: MM01 create material / CS01 create BOM ─────────────────────────
// Direct port of new_packaging_code.bas's Create_mm01 screen sequence.

    // industrySector -> RMMG1-MBRSH. Confirmed live: MM01 rejects with "Enter
    // an industry sector" without this field set, even when a reference
    // material is supplied on the same screen -- unlike the real SAP GUI
    // dialog (which auto-populates MBRSH from the reference material once
    // it's typed in), BDC/call-transaction requires every screen field to be
    // supplied explicitly. Callers should read the reference material's own
    // MARA-MBRSH (BuildMaraRequest/ParseMara) and pass that value through
    // rather than guessing a fixed industry-sector code -- this endpoint
    // creates real, permanent material masters, so silently baking in a
    // wrong guess is worse than requiring the caller supply a confirmed one.
    internal static RfcRequest BuildCreateMaterialRequest(string material, string referenceMaterial, string industrySector) =>
        BdcBuilder.For("MM01")
            .Screen("SAPLMGMM", "0060")
                .Field("BDC_OKCODE", "=AUSW")
                .Field("RMMG1-MATNR", material)
                .Field("RMMG1-MBRSH", industrySector)
                .Field("RMMG1-MTART", "VERP")
                .Field("RMMG1_REF-MATNR", referenceMaterial)
            .Screen("SAPLMGMM", "0070")
                .Field("BDC_OKCODE", "/00")
                .Field("MSICHTAUSW-KZSEL(01)", "X")
                .Field("MSICHTAUSW-KZSEL(03)", "X")
                .Field("MSICHTAUSW-KZSEL(07)", "X")
                .Field("MSICHTAUSW-KZSEL(08)", "X")
                .Field("MSICHTAUSW-KZSEL(09)", "X")
                .Field("BDC_OKCODE", "=ENTR")
            .Screen("SAPLMGMM", "0080")
                .Field("BDC_OKCODE", "=ENTR")
                .Field("RMMG1-WERKS", Plant)
                .Field("RMMG1-LGORT", "9912")
                .Field("RMMG1-DISPR", "")
            .Screen("SAPLMGMM", "3005")
                .Field("BDC_OKCODE", "/00")
                .Field("MARA-MEINS", "EA")
                .Field("MARA-MATKL", "VERP")
                .Field("MARA-SPART", "01")
                .Field("MARA-MTPOS_MARA", "VERP")
                .Field("MARA-GEWEI", "KG")
            .Screen("SAPLMGMM", "3006")
                .Field("MARA-TRAGR", "0001")
                .Field("MARC-LADGR", "2000")
                .Field("MARA-VHART", "INSB")
                .Field("MARC-PRCTR", "9912")
            .Screen("SAPLMGMM", "3006")
                .Field("BDC_OKCODE", "/00")
                .Field("MARA-MEINS", "EA")
                .Field("MARC-DISMM", "ND")
                .Field("MARC-BESKZ", "F")
                .Field("MARC-LGPRO", "9912")
                .Field("MARC-LGFSB", "9912")
            .Screen("SAPLMGMM", "3006")
                .Field("BDC_OKCODE", "/00")
                .Field("MARC-PERKZ", "M")
                .Field("MARC-MTVFP", "ZZ")
            .Screen("SAPLMGMM", "3000")
                .Field("BDC_OKCODE", "/00")
                .Field("MARA-MEINS", "EA")
                .Field("MARA-IPRKZ", "D")
                .Field("MARA-GEWEI", "KG")
                .Field("MARC-PRCTR", "9912")
            .Screen("SAPLSPO1", "0300")
                .Field("BDC_OKCODE", "=YES")
            .Build();

    internal static RfcRequest BuildCreateBomRequest(string material, string component) =>
        BdcBuilder.For("CS01")
            .Screen("SAPLCSDI", "0100")
                .Field("BDC_OKCODE", "/00")
                .Field("RC29N-MATNR", material)
                .Field("RC29N-WERKS", Plant)
                .Field("RC29N-STLAN", "1")
            .Screen("SAPLCSDI", "0110")
                .Field("BDC_OKCODE", "/00")
                .Field("RC29K-BMENG", "1")
                .Field("RC29K-STLST", "1")
            .Screen("SAPLCSDI", "0111")
                .Field("BDC_OKCODE", "/00")
            .Screen("SAPLCSDI", "0140")
                .Field("BDC_OKCODE", "/00")
                .Field("RC29P-IDNRK(01)", component)
                .Field("RC29P-MENGE(01)", "1")
                .Field("RC29P-POSTP(01)", "L")
            .Screen("SAPLCSDI", "0130")
                .Field("BDC_OKCODE", "/00")
                .Field("RC29P-POSNR", "0010")
                .Field("RC29P-IDNRK", component)
                .Field("RC29P-MENGE", "1")
                .Field("RC29P-MEINS", "EA")
            .Screen("SAPLCSDI", "0131")
                .Field("BDC_OKCODE", "/00")
                .Field("RC29P-RVREL", "Z")
                .Field("RC29P-SANKA", "X")
            .Screen("SAPLCSDI", "0138")
                .Field("BDC_OKCODE", "/00")
            .Screen("SAPLCSDI", "0140")
                .Field("BDC_OKCODE", "=FCBU")
            .Build();


// ── Write: ZPACK_INSTR_MAINT (insert/update/delete a packaging assignment) ──

    internal static RfcRequest BuildPackInstrMaintRequest(PackagingInstrSaveRequest req)
    {
        var row = new Dictionary<string, object?>
        {
            ["MATNR"]  = SapPad.Pad(req.Material, 18),
            ["WERKS"]  = Plant,
            ["KUNNR"]  = string.IsNullOrWhiteSpace(req.Customer) ? "" : SapPad.Pad(req.Customer, 10),
            ["PALL_MATNR"]  = req.PackMaterial ?? "",
            // No scaling here -- PalletQty/SmallBoxQty are plain quantities;
            // NCo's typed SetValue stores the real numeric value directly,
            // no gram/kg-style conversion involved. See ParseZpackInstr's
            // comment for the corrected read-side counterpart -- ParseSapDecimal
            // alone already parses SAP's comma-decimal quantity text (e.g.
            // "300,000" meaning 300) correctly; no further division belongs
            // here or there.
            ["PALL_QTY"]    = req.PalletQty,
            ["SMBX_QTY"]    = req.SmallBoxQty,
            ["PACK_PROD"]   = req.PackProd    ? "X" : "",
            ["BOX_GEN"]     = req.BoxGen      ? "X" : "",
            ["BATCH_SPREAD"]= req.BatchSpread ? "X" : "",
            ["PART_MIX"]    = req.PartMix     ? "X" : "",
            // Trace flags only ever apply at plant level (blank customer) —
            // mirrors upd_pack_instr's "Update trace flags only on plant level".
            ["CHARGE_REQ"]   = string.IsNullOrWhiteSpace(req.Customer) && req.ChargeReq   ? "X" : "",
            ["TECHSTAT_REQ"] = string.IsNullOrWhiteSpace(req.Customer) && req.TechStatReq ? "X" : "",
            ["PNUM_REQ"]     = string.IsNullOrWhiteSpace(req.Customer) && req.PNumReq     ? "X" : "",
        };

        return new RfcRequestBuilder(FnPackMaint)
            .Import("SQL_ACTION", req.SqlAction)
            .Import("TEST", "")
            .TableRow("IT_ZPACK_INSTR", row)
            .ReadParam("RC")
            .ReadTable("IT_MESSAGES", "Text")
            .Build();
    }

    internal static (bool success, string message) ParsePackInstrMaintResult(RfcResponse response)
    {
        var rcOk = response.Parameters.TryGetValue("RC", out var rcVal)
                   && int.TryParse(rcVal?.ToString(), out var rc) && rc == 0;

        var message = response.Tables.TryGetValue("IT_MESSAGES", out var rows) && rows.Count > 0
            ? string.Join(" ", rows.Select(r => r.GetValueOrDefault("Text")?.ToString() ?? "").Where(s => s.Length > 0))
            : (rcOk ? "OK" : "Update failed (no message returned)");

        return (rcOk, message);
    }
}
