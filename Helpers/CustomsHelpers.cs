using SapServer.Models;

namespace SapServer.Helpers;

// ── Request models ────────────────────────────────────────────────────────────

public sealed record LipsRequest
{
    public List<string> Deliveries { get; init; } = [];
}

public sealed record LikpRequest
{
    public List<string> Deliveries { get; init; } = [];
}

public sealed record VbfaRequest
{
    public List<VbfaLine> Lines { get; init; } = [];
}

public sealed record VbfaLine(string Delivery, string Item);

public sealed record MarcRequest
{
    public List<string> Materials { get; init; } = [];
}

public sealed record Kna1Request
{
    public List<string> Customers { get; init; } = [];
}

// ── Response models ───────────────────────────────────────────────────────────

public sealed record LipsRow(string DeliveryNumber, string ItemNumber, string MaterialNumber, string Quantity);
public sealed record LikpRow(string DeliveryNumber, string Incoterms, string ConsigneeCode);
public sealed record VbfaRow(string DeliveryNumber, string ItemNumber, string InvoiceNumber, string InvoiceItem, string StatisticalValue);
public sealed record MarcRow(string MaterialNumber, string CommodityCode, string CountryOfOrigin);
public sealed record Kna1Row(string CustomerCode, string DestinationCountry);

// ── Helpers ───────────────────────────────────────────────────────────────────

internal static class CustomsHelpers
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string Plant        = "3012";

    private static readonly string[] LipsColumns = ["VBELN", "POSNR", "MATNR", "KCMENG"];
    private static readonly string[] LikpColumns = ["VBELN", "INCO1", "KUNNR"];
    // VBELV/POSNV are included for client-side filtering and echoed back in the response
    private static readonly string[] VbfaColumns = ["VBELV", "POSNV", "VBELN", "POSNN", "RFWRT"];
    private static readonly string[] MarcColumns  = ["MATNR", "STAWN", "HERKL"];
    private static readonly string[] Kna1Columns  = ["KUNNR", "LAND1"];

    // ── LIPS ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildLipsRequest(LipsRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LIPS" });

        foreach (var f in LipsColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LIPS", FIELDNAME = f });

        builder
            .WhereCondition($"LIPS~WERKS EQ '{Plant}'")
            .WhereCondition("LIPS~KCMENG GT '0'")
            .WhereCondition("LIPS~VBELN IN opt");

        foreach (var d in req.Deliveries)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LIPS", FIELDNAME = "VBELN",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(d, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static LipsRow[] ParseLipsRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= LipsColumns.Length)
            .Select(cols => new LipsRow(cols[0], cols[1], cols[2], cols[3]))
            .ToArray();
    }

    // ── LIKP ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildLikpRequest(LikpRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LIKP" });

        foreach (var f in LikpColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LIKP", FIELDNAME = f });

        builder.WhereCondition("LIKP~VBELN IN opt");

        foreach (var d in req.Deliveries)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LIKP", FIELDNAME = "VBELN",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(d, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static LikpRow[] ParseLikpRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= LikpColumns.Length)
            .Select(cols => new LikpRow(cols[0], cols[1], cols[2]))
            .ToArray();
    }

    // ── VBFA ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildVbfaRequest(VbfaRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "VBFA" });

        foreach (var f in VbfaColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "VBFA", FIELDNAME = f });

        // VBTYP_N = 'M' limits flow records to billing documents only
        builder
            .WhereCondition("VBFA~VBTYP_N EQ 'M'")
            .WhereCondition("VBFA~VBELV IN opt");

        foreach (var d in req.Lines.Select(l => l.Delivery).Distinct())
            builder.TableItemRow("value_list", new
            {
                TABNAME = "VBFA", FIELDNAME = "VBELV",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(d, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static VbfaRow[] ParseVbfaRows(RfcResponse response, VbfaRequest req)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        // Build a set of (padded delivery, padded item) pairs to filter the broader SAP result
        var filter = req.Lines
            .Select(l => (SapPad.Pad(l.Delivery, 10), SapPad.Pad(l.Item, 6)))
            .ToHashSet();

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= VbfaColumns.Length
                        && filter.Contains((SapPad.Pad(cols[0], 10), SapPad.Pad(cols[1], 6))))
            .Select(cols => new VbfaRow(cols[0], cols[1], cols[2], cols[3], cols[4]))
            .ToArray();
    }

    // ── MARC ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildMarcRequest(MarcRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MARC" });

        foreach (var f in MarcColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MARC", FIELDNAME = f });

        builder
            .WhereCondition($"MARC~WERKS EQ '{Plant}'")
            .WhereCondition("MARC~MATNR IN opt");

        foreach (var m in req.Materials)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "MARC", FIELDNAME = "MATNR",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(m, 18), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static MarcRow[] ParseMarcRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= MarcColumns.Length)
            .Select(cols => new MarcRow(cols[0], cols[1], cols[2]))
            .ToArray();
    }

    // ── KNA1 ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildKna1Request(Kna1Request req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "KNA1" });

        foreach (var f in Kna1Columns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "KNA1", FIELDNAME = f });

        builder.WhereCondition("KNA1~KUNNR IN opt");

        foreach (var c in req.Customers)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "KNA1", FIELDNAME = "KUNNR",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(c, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static Kna1Row[] ParseKna1Rows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= Kna1Columns.Length)
            .Select(cols => new Kna1Row(cols[0], cols[1]))
            .ToArray();
    }
}
