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

public sealed record VbrkRequest
{
    public List<string> Invoices { get; init; } = [];
}

public sealed record ConsignmentPriceLine(string Customer, string Material);

public sealed record ConsignmentPriceRequest
{
    public List<ConsignmentPriceLine> Lines { get; init; } = [];
}

// ── Response models ───────────────────────────────────────────────────────────

public sealed record LipsRow(string DeliveryNumber, string ItemNumber, string MaterialNumber, string Quantity);
public sealed record LikpRow(string DeliveryNumber, string Incoterms, string ConsigneeCode);
public sealed record VbfaRow(string DeliveryNumber, string ItemNumber, string InvoiceNumber, string InvoiceItem, string StatisticalValue, string InvoiceDate);
public sealed record MarcRow(string MaterialNumber, string CommodityCode, string CountryOfOrigin);
public sealed record Kna1Row(string CustomerCode, string Name, string Street, string City, string PostCode, string DestinationCountry, string TransportZone, string VatNumber = "", string Incoterms = "");
public sealed record VbrkRow(string InvoiceNumber, string Currency);
public sealed record ConsignmentPriceRow(string CustomerCode, string MaterialNumber, string Rate, string Currency, string PricingUnit);

// ── Helpers ───────────────────────────────────────────────────────────────────

internal static class CustomsHelpers
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string Plant        = "3012";

    private static readonly string[] LipsColumns = ["VBELN", "POSNR", "MATNR", "KCMENG"];
    private static readonly string[] LikpColumns = ["VBELN", "INCO1", "KUNNR"];
    // VBELV/POSNV are included for client-side filtering and echoed back in the response.
    // ERDAT ("created on") is the workbook macro's own source for Invoice Date on the
    // CUSTOMS report (confirmed against its VBFA_Lookup routine's field list: VBELN,
    // POSNN, RFWRT, ERDAT — labelled "invoice"/"item"/"value"/"date" respectively).
    private static readonly string[] VbfaColumns = ["VBELV", "POSNV", "VBELN", "POSNN", "RFWRT", "ERDAT"];
    private static readonly string[] MarcColumns  = ["MATNR", "STAWN", "HERKL"];
    // KUNNR/LAND1 were the original fields (customer number + country); NAME1/STRAS/
    // ORT01/PSTLZ/LZONE were added to auto-fill the local Destinations table when a
    // picksheet references a customer we don't have on file yet — see the Node-side
    // /sap-sync route in deliverymain.js. LZONE (SAP transportation zone) maps to
    // our destinationZone field.
    // STCEG is the EU VAT registration number (VAT ID) held on the customer master —
    // the live-SAP source for CUSTOMS report VAT No., checked before falling back to
    // the admin-maintained CustomsVatNumberOverrides table on the Node side.
    private static readonly string[] Kna1Columns  = ["KUNNR", "NAME1", "STRAS", "ORT01", "PSTLZ", "LAND1", "LZONE", "STCEG"];
    // KNVV (customer master sales data) — INCO1 is the customer's default Incoterms
    // code for this sales org, used to pre-fill Destinations.defaultIncoterms on
    // auto-create alongside the KNA1 fields above. Scoped to our one sales org
    // (Plant, reused as VKORG — same convention as LogisticsHelpers.BuildPicksheetRequest)
    // since KNVV is keyed by KUNNR+VKORG+VTWEG+SPART and a customer can have several
    // sales-area rows; VTWEG/SPART aren't filtered, so ParseKnvvIncoterms just keeps
    // the first INCO1 it sees per customer.
    private static readonly string[] KnvvColumns  = ["KUNNR", "VKORG", "INCO1"];

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
            .WhereCondition("LIPS~KCMENG > 0")
            .WhereCondition("LIPS~VBELN IN opt");

        foreach (var d in req.Deliveries)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "LIPS",
                FIELDNAME = "VBELN",
                SIGN = "I",
                OPTION = "",
                LOW = SapPad.Pad(d, 10),
                HIGH = ""
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
            .Select(cols => new VbfaRow(cols[0], cols[1], cols[2], cols[3], cols[4], cols[5]))
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
            .Select(cols => new Kna1Row(
                CustomerCode:       cols[0],
                Name:               cols[1],
                Street:             cols[2],
                City:               cols[3],
                PostCode:           cols[4],
                DestinationCountry: cols[5],
                TransportZone:      cols[6],
                VatNumber:          cols[7]))
            .ToArray();
    }

    // ── VBRK ──────────────────────────────────────────────────────────────────
    // Only VBELN/WAERK — the workbook macro's own evidence for this lookup is a
    // single ambiguous comment block (never confirmed as an actually-executed
    // RFC_READ_TABLE call, unlike every other lookup in this file), so FKDAT
    // (invoice date — originally added here speculatively) was dropped in favour
    // of VBFA.ERDAT above, which IS confirmed against real executable macro code.
    // WAERK is kept regardless: it is the objectively correct SAP field for a
    // billing document's currency, so this call is a safe, useful addition even
    // though it may not byte-for-byte replicate whatever the macro itself does.

    private static readonly string[] VbrkColumns = ["VBELN", "WAERK"];

    internal static RfcRequest BuildVbrkRequest(VbrkRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "VBRK" });

        foreach (var f in VbrkColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "VBRK", FIELDNAME = f });

        builder.WhereCondition("VBRK~VBELN IN opt");

        foreach (var i in req.Invoices)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "VBRK", FIELDNAME = "VBELN",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(i, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    internal static VbrkRow[] ParseVbrkRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= VbrkColumns.Length)
            .Select(cols => new VbrkRow(cols[0], cols[1]))
            .ToArray();
    }

    // ── Consignment pricing (A005/KONP) ──────────────────────────────────────
    // For consignment customers, goods ship without a commercial invoice (a
    // manual/proforma document is used for transport) — VBFA has nothing to
    // return for those delivery lines. The source Excel macro's GetConsignmentValue
    // routine falls back to SAP's standard pricing-condition lookup for exactly
    // this case: A005 (customer/material condition access table) for the
    // condition record (KNUMH), then KONP (condition item — KBETR rate, KONWA
    // currency, KPEIN pricing unit) keyed by that KNUMH. Sales Value = KBETR *
    // quantity / KPEIN.
    //
    // Confirmed directly against the macro's actual behavior: this is NOT a
    // single RFC_READ_TABLE call with a SAP-side join — it's two separate,
    // unjoined queries (find the condition record, then find the value),
    // joined here in memory instead. An earlier version of this code used a
    // single joined call (A005 TAB_FROM/TAB_TO KONP via join_FIELDS); that
    // was never confirmed to work against the real ZRFC_READ_TABLES Z-RFC and
    // was returning nothing in production — this two-step version replicates
    // what the macro itself actually does.
    //
    // Step 1 (A005) batches via an OR'd set of literal EQ pairs rather than an
    // IN opt/value_list — the macro's own SAP calls build single literal
    // KUNNR EQ '..' AND MATNR EQ '..' conditions per pair (looped one at a
    // time), and there's no evidence the underlying Z-RFC supports two
    // independent IN opt filters on two different fields in the same call.
    // Step 2 (KONP) is a normal IN opt/value_list on KNUMH, same pattern as
    // every other VBELN-keyed lookup in this file. No KSCHL (condition type)
    // filter on step 1 either — the macro's own calls don't filter on it; if
    // more than one condition record matches a pair, ParseA005Rows keeps only
    // the first.

    private static readonly string[] A005Columns = ["KUNNR", "MATNR", "KNUMH"];
    private static readonly string[] KonpColumns  = ["KNUMH", "KBETR", "KONWA", "KPEIN"];

    internal static RfcRequest BuildA005Request(ConsignmentPriceRequest req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "A005" });

        foreach (var f in A005Columns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "A005", FIELDNAME = f });

        var pairs = req.Lines
            .Select(l => $"(A005~KUNNR EQ '{SapPad.Pad(l.Customer, 10)}' AND A005~MATNR EQ '{SapPad.Pad(l.Material, 18)}')")
            .ToArray();

        if (pairs.Length > 0)
            builder.WhereCondition($"({string.Join(" OR ", pairs)})");

        builder.WhereCondition($"A005~DATBI GT '{DateTime.Now:yyyyMMdd}'");

        return builder.ReadTable("data_display").Build();
    }

    // (CustomerCode, MaterialNumber, ConditionRecord) — one row per pair, first
    // condition record found kept if more than one matches.
    internal static (string CustomerCode, string MaterialNumber, string ConditionRecord)[] ParseA005Rows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var rows))
            return [];

        return SapDelimitedParser
            .ParseRows(rows, '|', skipHeader: true)
            .Where(cols => cols.Length >= A005Columns.Length)
            .Select(cols => (CustomerCode: cols[0], MaterialNumber: cols[1], ConditionRecord: cols[2]))
            .GroupBy(r => (r.CustomerCode, r.MaterialNumber))
            .Select(g => g.First())
            .ToArray();
    }

    internal static RfcRequest BuildKonpRequest(IEnumerable<string> conditionRecords)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "KONP" });

        foreach (var f in KonpColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "KONP", FIELDNAME = f });

        builder.WhereCondition("KONP~KNUMH IN opt");

        foreach (var k in conditionRecords.Distinct())
            builder.TableItemRow("value_list", new
            {
                TABNAME = "KONP", FIELDNAME = "KNUMH",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(k, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    // ConditionRecord (KNUMH) -> (Rate, Currency, PricingUnit)
    internal static Dictionary<string, (string Rate, string Currency, string PricingUnit)> ParseKonpRows(RfcResponse response)
    {
        var dict = new Dictionary<string, (string, string, string)>();

        if (!response.Tables.TryGetValue("data_display", out var rows))
            return dict;

        foreach (var cols in SapDelimitedParser.ParseRows(rows, '|', skipHeader: true))
        {
            if (cols.Length < KonpColumns.Length) continue;
            dict.TryAdd(cols[0], (cols[1], cols[2], cols[3]));
        }

        return dict;
    }

    // ── KNVV ──────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildKnvvRequest(Kna1Request req)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "KNVV" });

        foreach (var f in KnvvColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "KNVV", FIELDNAME = f });

        builder.WhereCondition($"KNVV~VKORG EQ '{Plant}'");
        builder.WhereCondition("KNVV~KUNNR IN opt");

        foreach (var c in req.Customers)
            builder.TableItemRow("value_list", new
            {
                TABNAME = "KNVV", FIELDNAME = "KUNNR",
                SIGN = "I", OPTION = "EQ", LOW = SapPad.Pad(c, 10), HIGH = ""
            });

        return builder.ReadTable("data_display").Build();
    }

    // Keyed by customer code -> Incoterms (INCO1). A customer can have several
    // KNVV rows (one per distribution channel/division within our sales org) —
    // this keeps the first INCO1 seen per customer rather than trying to pick a
    // "right" one, since Destinations.defaultIncoterms is a single best-effort
    // default the user can always correct manually.
    internal static Dictionary<string, string> ParseKnvvIncoterms(RfcResponse response)
    {
        var dict = new Dictionary<string, string>();

        if (!response.Tables.TryGetValue("data_display", out var rows))
            return dict;

        foreach (var cols in SapDelimitedParser.ParseRows(rows, '|', skipHeader: true))
        {
            if (cols.Length < KnvvColumns.Length) continue;

            var customerCode = cols[0];
            var incoterms    = cols[2];
            if (string.IsNullOrWhiteSpace(customerCode)) continue;

            dict.TryAdd(customerCode, incoterms);
        }

        return dict;
    }
}
