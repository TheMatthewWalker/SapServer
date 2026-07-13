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
                ComponentQty =      decimal.TryParse(cols[4], out var qty) ? qty : 0m,
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
                KgConversion =     decimal.TryParse(cols[1], out var kg) ? kg : 0m,
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
                Quantity =         decimal.TryParse(cols[2], out var qty) ? qty : 0m,
            })
            .ToArray();
    }


// ── Backflush ZF40N ──────────────────────────────────────────────────────

    internal static RfcRequest BuildZf40nRequest(Zf40nRequest body, bool requiresCharge) =>
        BdcBuilder.For("ZF40N")
            .Screen("SAPMZF40N", "0200")
                .Field("ST_FLD1-MATNR",   (body.Material ?? "").ToUpperInvariant())
                .Field("BDC_OKCODE",    "/00")
            .Screen("SAPMZF40N", "0200")
                .FieldIf(requiresCharge, "ST_FLD1-ACHARG", body.Header.Substring(0, 10) ?? "")
                .Field("ST_FLD1-BKTXT",    body.Header ?? "")
                .Field("ST_FLD1-ERFMG",    body.Quantity )
                .FieldIf(string.IsNullOrEmpty(body.Customer) && !string.IsNullOrEmpty(body.Packaging), "ST_ZMARA_C_T-MATNR", "IB_363643_" + body.Packaging )
                .FieldIf(!string.IsNullOrEmpty(body.Customer) && !string.IsNullOrEmpty(body.Packaging), "ST_ZMARA_C_T-MATNR", "IB_" + body.Customer + "_" + body.Packaging )
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
                .Field("RM07M-MTSNR", body.ScrapReason)
                .Field("RM07M-WERKS", Plant)
                .Field("RM07M-GRUND", body.ScrapReason)
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
