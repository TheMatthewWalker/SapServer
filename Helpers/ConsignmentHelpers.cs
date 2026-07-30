using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Vendor Consignment Tracker — goods-receipt pull for the three vendors who
/// supply raw material on consignment (Chemours, Fothergill/FCF, Raaj).
/// Replaces the manually-typed "GR" tab of their old per-vendor Excel
/// workbooks. Live consignment stock (MKOL SLABS) deliberately reuses
/// PerformanceHelpers.BuildConsignmentStockRequest/ParseConsignmentStockRows
/// rather than being duplicated here — that query is already plant-wide and
/// vendor-agnostic, and since each of these materials belongs to exactly one
/// consignment vendor, matching on Material after the fact is sufficient; no
/// need for a second LIFNR-filtered stock query.
///
/// "Undeclared consumption" per material is computed as a BALANCE in Node
/// (delivered − live stock − already-declared), not pulled as a raw SAP
/// withdrawal-movement query — see migrate_consignment_tracker.sql's header
/// comment for why: the exact movement type SAP uses for a consignment
/// withdrawal at this plant hasn't been confirmed against a live SAP GUI
/// session, and guessing wrong here would produce an incorrect declaration
/// used to invoice a real supplier. GR (this file) and stock (MKOL, already
/// proven) are the only two consignment data points this phase relies on.
/// </summary>
internal static class ConsignmentHelpers
{
    internal const string FnReadTables = "ZRFC_READ_TABLES";
    internal const string Plant        = "3012";

    // MATNR, MBLNR, ZEILE, MENGE, MEINS, LIFNR, LFBNR, SHKZG (all MSEG), then
    // BLDAT, BUDAT (both MKPF). LFBNR (vendor delivery note number) is the
    // standard SAP field for the vendor's own reference at goods receipt,
    // and is what the old workbooks' "Invoice Number" column (e.g. "RMIE
    // 0041") was always hand-typed from — it's an MSEG-level field (document
    // number of a reference document, one per line), NOT MKPF as originally
    // assumed here; registering it against MKPF made ZRFC_READ_TABLES reject
    // the whole query with FIELD_NOT_VALID at runtime (2026-07-30 sync of
    // vendor 4) — that error is what surfaced the mistake.
    //
    // SHKZG (debit/credit indicator — 'S' = stock increase, 'H' = stock
    // decrease) signs the quantity for movement 102 (GR reversal — someone
    // posted a 101 by mistake and reversed it). Deliberately reading this
    // straight from SAP rather than assuming "BWART 102 always means
    // negative" — same reasoning as everywhere else in this file: a wrong
    // guess here would misstate a vendor's delivered total.
    private static readonly string[] MsegColumns = ["MATNR", "MBLNR", "ZEILE", "MENGE", "MEINS", "LIFNR", "LFBNR", "SHKZG"];
    private static readonly string[] MkpfColumns  = ["BLDAT", "BUDAT"];

    /// <summary>
    /// Pulls consignment goods-receipt lines (movement 101 — GR — and its
    /// reversal, movement 102, SOBKZ=K) for one vendor. Including 102 keeps
    /// a mistakenly-posted-then-reversed GR from inflating the vendor's
    /// delivered total: the reversal comes back as its own MSEG line (its
    /// own MaterialDocument), signed negative via SHKZG so
    /// SUM(ConsignmentDelivery.Quantity) nets to what SAP actually shows.
    /// <paramref name="sapVendorNumber"/> should be the raw
    /// dbo.Vendor.SapVendorNumber value (padding is handled here, matching
    /// SapPad.Pad's idempotent-on-already-padded-values behaviour used
    /// everywhere else in this codebase). <paramref name="materials"/> is
    /// the vendor's known material list (Node's dbo.VendorMaterial) and is
    /// the primary/selective filter here — see the WHERE-building comment
    /// below for why. <paramref name="sinceDate"/> is an optional
    /// posting-date floor (SAP dd.mm.yyyy format) to keep the daily re-sync
    /// cheap once a vendor has years of history — omit it for a vendor's
    /// first-ever sync to pull everything.
    /// </summary>
    internal static RfcRequest BuildVendorGrRequest(string sapVendorNumber, IReadOnlyList<string> materials, string? sinceDate = null)
    {
        if (materials.Count == 0)
            throw new ArgumentException("At least one material is required — see ConsignmentController.GetVendorGr.", nameof(materials));

        var vendor = SapPad.Pad(sapVendorNumber, 10);

        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MSEG" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MKPF" });

        foreach (var f in MsegColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = f });
        foreach (var f in MkpfColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MKPF", FIELDNAME = f });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MSEG", FLD_FROM = "MANDT", TAB_TO = "MKPF", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MSEG", FLD_FROM = "MBLNR", TAB_TO = "MKPF", FLD_TO = "MBLNR" });

        builder.WhereCondition($"MSEG~WERKS EQ '{Plant}'");

        // MATNR IN opt is deliberately the primary/selective filter here —
        // Matthew's own experience running equivalent reports directly in
        // SAP is that filtering MSEG on a known material list runs much
        // faster than filtering on LIFNR (2026-07-30; this is what fixed a
        // >3-minute first-sync timeout even after the BWART filter below was
        // already working correctly). Same IN-opt/value_list interface as
        // BWART — see that condition's comment for why literal SQL text
        // doesn't work with this custom RFC. OPTION left blank (not "EQ")
        // for each value, matching CostingHelper.BuildCostSheetRequest's
        // already-proven MATNR IN opt usage exactly.
        builder.WhereCondition("MSEG~MATNR IN opt");
        foreach (var material in materials)
        {
            builder.TableItemRow("value_list", new
            {
                TABNAME   = "MSEG",
                FIELDNAME = "MATNR",
                SIGN      = "I",
                OPTION    = "",
                LOW       = SapPad.Pad(material, 18),
                HIGH      = ""
            });
        }

        builder
            // Two dead ends before landing on IN opt (2026-07-30), both
            // worth recording so nobody retries them: (1) "( MSEG~BWART EQ
            // '101' OR MSEG~BWART EQ '102' )" — a parenthesised OR inside
            // one .WhereCondition() call — came back pulled=0 (not even the
            // pre-existing 101 lines), failing silently rather than raising
            // FIELD_NOT_VALID. (2) The literal SQL fragment
            // "MSEG~BWART IN ('101','102')" — also pulled=0. Both assumed
            // ZRFC_READ_TABLES's dynamic WHERE accepts arbitrary literal
            // Open-SQL text; it doesn't. This custom RFC has a dedicated
            // extended interface for IN-type conditions instead: the
            // condition text is just "IN opt", and the actual value(s) go in
            // a separate "value_list" input table (TABNAME/FIELDNAME/SIGN/
            // OPTION/LOW/HIGH — the classic RFC_READ_TABLE OPTIONS
            // structure). SIGN=I ("Include"), OPTION=BT ("Between") with
            // LOW='101'/HIGH='102' covers both movement types in a single
            // value_list row since 101 and 102 are the only two valid values
            // in that range.
            .WhereCondition("MSEG~BWART IN opt")
            .WhereCondition("MSEG~SOBKZ EQ 'K'")
            // Kept as a belt-and-suspenders filter now that MATNR is doing
            // the selective work above — cheap once MATNR has already
            // narrowed the result set, and guards against a material ever
            // being mis-assigned to the wrong vendor in dbo.VendorMaterial.
            .WhereCondition($"MSEG~LIFNR EQ '{vendor}'")
            .TableItemRow("value_list", new
            {
                TABNAME   = "MSEG",
                FIELDNAME = "BWART",
                SIGN      = "I",
                OPTION    = "BT",
                LOW       = "101",
                HIGH      = "102"
            });

        if (!string.IsNullOrWhiteSpace(sinceDate))
            builder.WhereCondition($"MKPF~BUDAT GE '{sinceDate}'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static ConsignmentGrRow[] ParseVendorGrRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        var expectedCols = MsegColumns.Length + MkpfColumns.Length;

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= expectedCols)
            .Select(cols =>
            {
                // Column order follows registration order: MsegColumns
                // (MATNR, MBLNR, ZEILE, MENGE, MEINS, LIFNR, LFBNR, SHKZG)
                // then MkpfColumns (BLDAT, BUDAT).
                var rawQty = decimal.TryParse(cols[3].Trim(), out var qty) ? qty : 0m;
                var shkzg  = cols[7].Trim();
                // 'H' = credit / stock decrease — a 102 reversal. 'S' (or
                // anything else, defensively) = debit / stock increase, the
                // normal 101 GR. MENGE itself always comes back positive
                // from SAP; SHKZG is what carries the direction.
                var signedQty = shkzg == "H" ? -rawQty : rawQty;

                return new ConsignmentGrRow
                {
                    Material         = PerformanceHelpers.NormaliseMaterial(cols[0]),
                    MaterialDocument = cols[1],
                    MaterialDocItem  = cols[2],
                    Quantity         = signedQty,
                    Uom              = cols[4],
                    Vendor           = cols[5],
                    InvoiceNumber    = cols[6],
                    DocumentDate     = cols[8],
                    PostingDate      = cols[9],
                };
            })
            .ToArray();
    }
}
