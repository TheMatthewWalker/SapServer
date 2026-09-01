using SapServer.Models;
using SapServer.Models.Bapi;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SapServer.Helpers;

internal static class ProductionHelpers
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreate = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    // Real named RFC (not the generic ZRFC_READ_TABLES wrapper every other
    // lookup in this file goes through) — order/item special-instruction text
    // is process-critical per the user's explicit instruction, so this reads
    // it live from SAP on every ticket print rather than via a cached table.
    internal const string FnReadText = "RFC_READ_TEXT";

    // Text ID for the "special instructions" text saved against a sales item
    // (STXH-TDID). Matches the counter value used by the existing Excel VBA
    // READ_TEXT() macro this replaces.
    internal const string SpecialInstructionsTextId = "004";

    // Column order must exactly match query_FIELDS registration order below
    internal static readonly string[] BomColumns =
        ["MATNR", "WERKS", "IDNRK", "POSNR", "MENGE", "MEINS", "LGORT", "PRVBE"];

// ── BOM ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildBomRequest(BomQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  query.RowCount)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZBOM_INFO" });

        foreach (var field in BomColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZBOM_INFO", FIELDNAME = field });

        builder.WhereCondition($"ZBOM_INFO~WERKS EQ '{Plant}'");

        if (!string.IsNullOrWhiteSpace(query.Material))
            builder.WhereCondition($"ZBOM_INFO~MATNR EQ '{(SapPad.Pad(query.Material, 18) ?? "").ToUpperInvariant()}'");

        if (!string.IsNullOrWhiteSpace(query.Component))
            builder.WhereCondition($"ZBOM_INFO~IDNRK EQ '{(SapPad.Pad(query.Component, 18) ?? "").ToUpperInvariant()}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    // Bulk variant — one round trip for N materials instead of N calls to BuildBomRequest
    // above. Same IN opt / value_list pattern as BuildProfitCentresRequest below (deliberately
    // not ROWCOUNT-limited, same as that proven-working call) — used by
    // MrpAnalysisHelper.ExplodeBom to explode every material at one BOM depth in a single
    // call. Reuses ParseBomRows unchanged; it doesn't care how many distinct MATNR values
    // were in the WHERE clause.
    internal static RfcRequest BuildBomRequestBulk(IEnumerable<string> materials)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZBOM_INFO" });

        foreach (var field in BomColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "ZBOM_INFO", FIELDNAME = field });

        builder
            .WhereCondition($"ZBOM_INFO~WERKS EQ '{Plant}'")
            .WhereCondition("ZBOM_INFO~MATNR IN opt");

        foreach (var m in materials)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "ZBOM_INFO", FIELDNAME = "MATNR",
                SIGN = "I", OPTION = "EQ", LOW = (SapPad.Pad(m, 18) ?? "").ToUpperInvariant(), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static BomRow[] ParseBomRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= BomColumns.Length)
            .Select(cols => new BomRow
            {
                Material =          cols[0],
                Plant =             cols[1],
                Component =         cols[2],
                Item =              cols[3],
                ComponentQty =      RfcRowExtensions.ParseSapDecimal(cols[4]) ?? 0m,
                ComponentUnit =     cols[5],
                StorageLocation =   cols[6],
                SupplyArea =        cols[7]
            })
            .ToArray();
    }


    internal static RfcRequest BuildKgToUnitRequest(KgToUnitQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  "1")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARA" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = "MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARA", FIELDNAME = "BRGEW" });
        builder.WhereCondition($"MARA~MATNR EQ '{(SapPad.Pad(query.Material, 18) ?? "").ToUpperInvariant()}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


    internal static KgToUnitRow[] ParseKgToUnit(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 2)
            .Select(cols => new KgToUnitRow
            {
                Material =         cols[0],
                KgConversion =     RfcRowExtensions.ParseSapDecimal(cols[1]) ?? 0m,
            })
            .ToArray();
    }




// ── Material Document ─────────────────────────────────────────────────────────────────
    internal static MsegRow[] ParseMaterialDocument(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 3)
            .Select(cols => new MsegRow
            {
                StorageLocation =  cols[0],
                Material =         cols[1],
                Quantity =         RfcRowExtensions.ParseSapDecimal(cols[2]) ?? 0m,
            })
            .ToArray();
    }


// ── Backflush ZF40N ──────────────────────────────────────────────────────

    // The packaging-instruction material ZF40N books the finished good's
    // packaging against — "IB_363643_<code>" for stock builds with no named
    // customer, "IB_<customer>_<code>" otherwise. Also the exact value
    // Z_ZPRODBATCH_MAINT needs for ZPRODBATCH_TBL~PALL_MATNR (see
    // BuildProdBatchMaintRequest below), so it's shared rather than
    // reconstructed a second time — the combined drumming-backflush endpoint
    // computes it once and passes it to both.
    internal static string BuildPackagingInstruction(string? customer, string? packaging) =>
        string.IsNullOrEmpty(packaging) ? "" :
        string.IsNullOrEmpty(customer)  ? $"IB_363643_{packaging}" : $"IB_{customer}_{packaging}";

    internal static RfcRequest BuildZf40nRequest(Zf40nRequest body, bool requiresCharge) =>
        BdcBuilder.For("ZF40N")
            .Screen("SAPMZF40N", "0200")
                .Field("ST_FLD1-MATNR",   (body.Material ?? "").ToUpperInvariant())
                .Field("BDC_OKCODE",    "/00")
            .Screen("SAPMZF40N", "0200")
                .FieldIf(requiresCharge, "ST_FLD1-ACHARG", body.Header.Substring(0, 10) ?? "")
                .Field("ST_FLD1-BKTXT",    body.Header ?? "")
                .Field("ST_FLD1-ERFMG",    body.Quantity )
                .FieldIf(!string.IsNullOrEmpty(body.Packaging), "ST_ZMARA_C_T-MATNR", BuildPackagingInstruction(body.Customer, body.Packaging))
                .Field("BDC_OKCODE", "=SAVE")
            .Build();


// ── Reverse Backflush MF41 ──────────────────────────────────────────────────────

    internal static RfcRequest BuildMf41Request(Mf41Request body) =>
        BdcBuilder.For("MF41")
            .Screen("SAPLBARM", "0400")
                .Field("RM61A-RTYPO",   "X")
                .Field("BDC_OKCODE",    "=LAGER")
            .Screen("SAPLBARM", "0400")
                .Field("RM07M-MBLNR",    body.MaterialDocument ?? "")
                .Field("BDC_OKCODE", "=EXEC")
            .Build();




// ── Create Scrap Entry MB11 ──────────────────────────────────────────────────────

    internal static RfcRequest BuildBomScrapRequest(BomScrapRequest body) =>
        BdcBuilder.For("MB11")
            .Screen("SAPMM07M", "0400")
                .Field("MKPF-BKTXT", body.Header ?? "")
                .Field("RM07M-BWARTWA", body.MovementType)
                .Field("RM07M-MTSNR", body.ScrapReason ?? "")
                .Field("RM07M-WERKS", Plant)
                .Field("RM07M-GRUND", body.ScrapReason ?? "")
                .Field("RM07M-LGORT", body.StorageLocation)
                .Field("XFULL",   "X")
                .Field("RM07M-XNAPR", "X")
                .Field("RM07M-WVERS1", "X")
                .Field("BDC_OKCODE",    "/00")
            .Screen("SAPMM07M", "0421")
                .Field("MSEG-MATNR(01)", (SapPad.Pad(body.Material, 18) ?? "").ToUpperInvariant())
                .Field("MSEG-ERFMG(01)", body.Quantity)
                .Field("MSEG-ERFME(01)", body.ComponentUnit)
                //.Field("DKACB-FMORE", "X")
                .Field("BDC_OKCODE", "=BU")
            .Screen("SAPLKACB", "0002")
                .Field("COBL-AUFNR", body.ProfitCentre)
                .Field("BDC_OKCODE", "=ENTE")
            .Screen("SAPLKACB", "0002")
                .Field("BDC_OKCODE", "=ENTE")
            .Build();



// ── Reverse Scrap MBST ──────────────────────────────────────────────────────

    internal static RfcRequest BuildMbstRequest(Mf41Request body) =>
        BdcBuilder.For("MBST")
            .Screen("SAPMM07M", "0460")
                .Field("BDC_OKCODE", "/00")
                //.Field("MKPF-BUDAT", Format(Now(), "dd.mm.yyyy")) // posting date
                .Field("RM07M-MBLNR", body.MaterialDocument)
                .Field("XFULL", "X")
                .Field("RM07M-XNAPR", "X")
                .Field("RM07M-WVERS2", "X")
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE", "=BU")
            .Screen("SAPLKACB", "0002")
                .Field("BDC_OKCODE", "=ENTE")
            .Screen("SAPLKACB", "0002")
                .Field("BDC_OKCODE", "=ENTE")
            .Build();


// ── Material Validation (Read Tables) ──────────────────────────────────────────────────────

    internal static RfcRequest BuildRequiresCharge(string? material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "ZPACK_INSTR" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "ZPACK_INSTR", FIELDNAME = "CHARGE_REQ" });

        builder.WhereCondition($"ZPACK_INSTR~MATNR EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


    internal static RfcRequest BuildStorageLocation(string? material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "LGPRO" });

        builder.WhereCondition($"MARC~MATNR EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");
        builder.WhereCondition($"MARC~WERKS EQ '{Plant}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    // Finds the original backflush (movement 131) document for a batch — the
    // first step of the re-drum reversal chain: a batch-managed product being
    // returned to SA/PTFE via Staging Post needs its original consumption
    // reversed via MF41 before the return means anything in SAP.
    internal static RfcRequest BuildFindBackflushDocumentRequest(string? batch)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MSEG" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MBLNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MENGE" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "LGORT" });

        builder.WhereCondition($"MSEG~CHARG EQ '{(batch ?? "").ToUpperInvariant()}'");
        builder.WhereCondition($"MSEG~BWART EQ '131'");
        builder.WhereCondition($"MSEG~WERKS EQ '{Plant}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    internal static BackflushDocumentRow[] ParseBackflushDocumentRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 4)
            .Select(cols => new BackflushDocumentRow
            {
                MaterialDocument = cols[0],
                Material         = cols[1],
                Quantity         = RfcRowExtensions.ParseSapDecimal(cols[2]) ?? 0m,
                StorageLocation  = cols[3],
            })
            .ToArray();
    }

    // The inverse of BuildFindBackflushDocumentRequest above — that one goes
    // CHARG -> MBLNR (movement 131) to support reversal; this one goes
    // MBLNR -> CHARG, to find the batch SAP assigned to the finished good a
    // backflush document was just posted for. Same movement type, since it's
    // the same underlying MSEG row — a ZF40N backflush's 131 line IS the
    // finished good's goods receipt, batch and all. MATNR is included in the
    // WHERE (not just parsed from the result) because a single backflush
    // document only ever carries the one finished material, and filtering
    // on it up front rules out any unrelated row sharing the same MBLNR.
    internal static RfcRequest BuildFindProducedBatchRequest(string? materialDocument, string? material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MSEG" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "CHARG" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MENGE" });

        builder.WhereCondition($"MSEG~MBLNR EQ '{materialDocument}'");
        builder.WhereCondition($"MSEG~BWART EQ '131'");
        builder.WhereCondition($"MSEG~WERKS EQ '{Plant}'");
        builder.WhereCondition($"MSEG~MATNR EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    internal static ProducedBatchRow[] ParseProducedBatchRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 3)
            .Select(cols => new ProducedBatchRow
            {
                Charge   = cols[0],
                Material = cols[1],
                Quantity = RfcRowExtensions.ParseSapDecimal(cols[2]) ?? 0m,
            })
            .ToArray();
    }


    // ── Z_ZPRODBATCH_MAINT (Drumming batch/pack table maintenance) ─────────────
    //
    // Ported from the legacy Z_ZPRODBATCH_MAINT_run VBA macro. Insert-only
    // (SQL_ACTION="I") — the macro's Read/Delete branches aren't needed here
    // since the only caller is the combined drumming-backflush endpoint,
    // called once per drum right after its batch is confirmed to exist.
    // Writes one row to each of two custom SAP tables per drum:
    //   - ZPRODBATCH_TBL: the produced batch itself (CHARG/MATNR/WERKS/
    //     PALL_MATNR/MBLNR). PALL_MATNR is the same packaging-instruction
    //     string ZF40N itself was given for ST_ZMARA_C_T-MATNR — see
    //     BuildPackagingInstruction above, shared rather than rebuilt.
    //   - ZBATCHPACK_TBL: the drum's outer packaging, keyed to the same
    //     CHARG. MATNR here is a *different* material — not the packaging
    //     instruction, but a fixed "P_..._NMT" packaging material looked up
    //     from the packcode via PackCodeToPackaging below (verbatim from the
    //     VBA's packcode If-chain). MENGE is always literal 1 (packaging
    //     count, not the drum's own produced quantity) per the source macro.
    internal const string FnProdBatchMaint = "Z_ZPRODBATCH_MAINT";

    internal static readonly IReadOnlyDictionary<string, string> PackCodeToPackaging =
        new Dictionary<string, string>
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

    internal static RfcRequest BuildProdBatchMaintRequest(
        string charge, string material, string packInstruction, string materialDocument, decimal weightKg, string packCode)
    {
        var packaging = PackCodeToPackaging.GetValueOrDefault((packCode ?? "").ToUpperInvariant(), "");

        var builder = new RfcRequestBuilder(FnProdBatchMaint)
            .Import("SQL_ACTION", "I")
            .Import("TEST", "");

        builder.TableRow("ZPRODBATCH_TBL", new
        {
            CHARG      = SapPad.Pad(charge, 10),
            MATNR      = SapPad.Pad(material, 18).ToUpperInvariant(),
            WERKS      = Plant,
            PALL_MATNR = SapPad.Pad(packInstruction, 18),
            MBLNR      = SapPad.Pad(materialDocument, 10),
            VBELN      = "",
        });

        builder.TableRow("ZBATCHPACK_TBL", new
        {
            CHARG   = SapPad.Pad(charge, 10),
            MATNR   = SapPad.Pad(packaging, 18),
            MENGE   = 1m,
            MEINS   = "EA",
            TAREWEI = weightKg,
            GEWEI   = "KG",
        });

        return builder
            .ReadParam("RC_BATCH")
            .ReadParam("RC_PACK")
            .Build();
    }

    internal static (string RcBatch, string RcPack) ParseProdBatchMaintResponse(RfcResponse response)
    {
        var rcBatch = ReturnTableHelper.GetParam(response, "RC_BATCH") ?? "";
        var rcPack  = ReturnTableHelper.GetParam(response, "RC_PACK") ?? "";
        return (rcBatch, rcPack);
    }


    internal static RfcRequest BuildMatDocRequest(string? materialDocument)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MSEG" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "LGORT" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = "MENGE" });

        builder.WhereCondition($"MSEG~MBLNR EQ '{materialDocument}'");
        builder.WhereCondition($"MSEG~WERKS EQ '{Plant}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


    // Finds the cost collector (repetitive manufacturing production order)
    // for a material — AFKO~PLNBEZ is the material a cost collector order
    // exists for, AUFNR is the order number itself. Used by the re-drum
    // reversal chain's WM tidy-up step: stock moved outside Warehouse
    // Management (e.g. by MF41) lands in bin type 901, bin = that order
    // number, zero-padded/truncated to 10 characters — mirrors the
    // existing get_CC() VB helper (table AFKO, filter PLNBEZ, column
    // AUFNR, Right(...,10)) exactly.
    internal static RfcRequest BuildCostCollector(string? material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "AFKO" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "AFKO", FIELDNAME = "AUFNR" });

        builder.WhereCondition($"AFKO~PLNBEZ EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


    internal static RfcRequest BuildProfitCentre(string? material)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "PRCTR" });

        builder.WhereCondition($"MARC~MATNR EQ '{(SapPad.Pad(material, 18) ?? "").ToUpperInvariant()}'");
        builder.WhereCondition($"MARC~WERKS EQ '{Plant}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    // Bulk variant — one round trip for N materials instead of N calls to
    // BuildProfitCentre above. Same IN opt / value_list pattern as
    // CustomsHelpers.BuildMarcRequest (that file's bulk MARC lookup, for a
    // different pair of columns) — deliberately not ROWCOUNT-limited, same
    // as that proven-working call, so every material's row comes back
    // regardless of how many were asked for.
    internal static RfcRequest BuildProfitCentresRequest(ProfitCentresRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" });

        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "MATNR" });
        builder.TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = "PRCTR" });

        builder
            .WhereCondition($"MARC~WERKS EQ '{Plant}'")
            .WhereCondition("MARC~MATNR IN opt");

        foreach (var m in req.Materials)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "MARC", FIELDNAME = "MATNR",
                SIGN = "I", OPTION = "EQ", LOW = (SapPad.Pad(m, 18) ?? "").ToUpperInvariant(), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static ProfitCentreRow[] ParseProfitCentreRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 2)
            .Select(cols => new ProfitCentreRow { Material = cols[0], ProfitCentre = cols[1] })
            .ToArray();
    }

    internal static RfcRequest SapRT(string? table, string[] fields, string[] where)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  1)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = table });

        foreach (var field in fields)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });

        foreach (var condition in where)
            builder.WhereCondition($"{table}~{condition}");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


// ── Text (RFC_READ_TEXT) ─────────────────────────────────────────────────
//
// Live lookup of SAP long-text (STXH/STXL) — used for the Drumming Ticket's
// "Special Instructions" section. TDNAME is the sales-item text key: a
// 10-char sales document number immediately followed by a 6-char item
// number (no separator), exactly matching the existing Excel VBA macro:
//   objData(objData.RowCount, "TDNAME") = Right("0000" & salesdoc, 10) & Right("0000" & item, 6)
// TDID defaults to "004" (special instructions) but is parameterised since
// other text IDs against the same object could be useful later.
//
// NOT the standard scalar-import RFC_READ_TEXT interface (OBJECT/NAME/ID/
// LANGUAGE as exports + output table LINES) — this SAP system's RFC_READ_TEXT
// as exposed via SAPFunctions64 instead takes a single INPUT table called
// TEXT_LINES (fields TDOBJECT/TDNAME/TDID/TDSPRAS on one row), and the same
// table doubles as the result — the OCX overwrites/fills it in place after
// Call(). Confirmed directly against SAP_Lookup_Mod.bas's READ_TEXT():
//   Set objData = objRfcFunc.Tables("TEXT_LINES")
//   objData.Rows.Add
//   objData(objData.RowCount, "TDOBJECT") = "VBBP"
//   objData(objData.RowCount, "TDNAME")   = <name>
//   objData(objData.RowCount, "TDID")     = counter
//   objData(objData.RowCount, "TDSPRAS")  = "E"
//   objRfcFunc.Call
//   arr = objData.Data : READ_TEXT = arr(1, 8)   ' column 8 = TDLINE
// Same dual-purpose-table pattern already used by BuildInvoicingRequest's
// SALE_HIST_T (see its comment) — TableRow(...) populates it as an input via
// func.Tables(name), ReadTable(...) reads the same table back afterward via
// func.tables.Item(name); both resolve to the same underlying COM collection.
// Using the previous OBJECT/NAME/ID/LANGUAGE scalar-export approach failed
// outright — those exports don't exist on this system's RFC_READ_TEXT, so
// func.exports("OBJECT") returned null and threw on the very first import.

    internal static RfcRequest BuildOrderTextRequest(string salesDocument, string item, string textId = SpecialInstructionsTextId)
    {
        var name = SapPad.Pad(salesDocument, 10) + SapPad.Pad(item, 6);

        return new RfcRequestBuilder(FnReadText)
            .TableRow("TEXT_LINES", new { TDOBJECT = "VBBP", TDNAME = name, TDID = textId, TDSPRAS = "E" })
            .ReadTable("TEXT_LINES", "TDLINE")
            .Build();
    }

    // A text can span several 132-char TLINE rows — join them all (trimmed)
    // rather than returning only the first, so nothing is silently truncated.
    // (The VBA only ever reads row 1; joining every row returned is a superset
    // of that behaviour, not a divergence — if SAP only ever echoes one row
    // back, this produces the exact same single-line result.)
    internal static string ParseOrderText(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("TEXT_LINES", out var rows) || rows.Count == 0)
            return "";

        return string.Join("\n", rows
            .Select(r => r.TryGetValue("TDLINE", out var v) ? v?.ToString()?.TrimEnd() ?? "" : "")
            .Where(s => s.Length > 0));
    }


// ── Message Parsing ──────────────────────────────────────────────────────

    internal static BdcResponse ParseBdcResponse(RfcResponse bdc)
    {
        var rawMessage = ReturnTableHelper.GetParam(bdc, "MESSG") ?? "";
        var messageMatch = Regex.Match(
            rawMessage,
            @"^(?<type>\S+)\s+(?<class>\S+)\s+(?<number>\S+)\s+(?<message>.*)$");

        var message = messageMatch.Success
            ? messageMatch.Groups["message"].Value.Trim()
            : rawMessage;

        var documentMatch = Regex.Match(message, @"\bdocument\s+(?<document>\d+)\b", RegexOptions.IgnoreCase);

        return new BdcResponse
        {
            Type           = messageMatch.Success ? messageMatch.Groups["type"].Value : "",
            MessageClass   = messageMatch.Success ? messageMatch.Groups["class"].Value : "",
            MessageNumber  = messageMatch.Success ? messageMatch.Groups["number"].Value : "",
            Message        = message,
            DocumentNumber = documentMatch.Success ? documentMatch.Groups["document"].Value : "",
            RawMessage     = rawMessage
        };
    }

    internal static string ParseSingleSapResult(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows) || rows.Count == 0)
            throw new InvalidOperationException("No results found.");

        var responseValue = SapDelimitedParser.ParseRows(rows, '|', skipHeader: true).FirstOrDefault()?.FirstOrDefault();
        return responseValue ?? "";
    }


    internal static bool ParseRequiresCharge(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows) || rows.Count == 0)
            throw new InvalidOperationException("No results found.");

        var responseValue = SapDelimitedParser.ParseRows(rows, '|', skipHeader: true).FirstOrDefault()?.FirstOrDefault();
        return !string.IsNullOrEmpty(responseValue);
    }

}
