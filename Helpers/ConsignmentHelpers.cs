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
    /// Pulls consignment goods-receipt lines for one vendor and ONE movement
    /// type (SOBKZ=K). Called twice by ConsignmentController.GetVendorGr —
    /// once with movementType="101" (GR) and once with movementType="102"
    /// (reversal of a mistakenly-posted GR) — and the two response sets are
    /// merged in C#, rather than trying to get SAP to match both movement
    /// types in a single call.
    ///
    /// This is a deliberate rollback (2026-07-30) after repeated failures
    /// trying to filter on more than one value per field in a single
    /// ZRFC_READ_TABLES call: neither "( MSEG~BWART EQ '101' OR MSEG~BWART
    /// EQ '102' )" (parenthesised OR), nor the literal "MSEG~BWART IN
    /// ('101','102')" SQL fragment, nor the RFC_READ_TABLE-style "IN opt" +
    /// value_list mechanism (used successfully elsewhere in this codebase
    /// for MATNR in CostingHelper.BuildCostSheetRequest, and by all
    /// appearances the textbook-correct approach) ever actually returned
    /// data here — every attempt came back with zero rows, silently, even
    /// for the pre-existing 101 lines that a plain single-value EQ filter
    /// had always returned correctly. Whatever the real cause (this
    /// function's dynamic-WHERE handling may simply not support multi-value
    /// conditions the way standard RFC_READ_TABLE does, or may not support
    /// stacking two independent IN-opt conditions — MATNR and BWART — in one
    /// call), the pragmatic fix is to stop fighting it: go back to the
    /// single-value "MSEG~BWART EQ '{value}'" condition that is proven to
    /// work, and get both movement types by calling twice. Also dropped, for
    /// the same reason, the MATNR-based filtering added earlier today for
    /// performance (SapServer d8bce68) — LIFNR EQ is the filter that's
    /// actually confirmed working, so that's what's back in use here.
    /// Revisit the materials-based speedup as its own isolated change once
    /// GR sync is confirmed reliable again.
    ///
    /// <paramref name="sapVendorNumber"/> should be the raw
    /// dbo.Vendor.SapVendorNumber value (padding is handled here, matching
    /// SapPad.Pad's idempotent-on-already-padded-values behaviour used
    /// everywhere else in this codebase). <paramref name="sinceDate"/> is an
    /// optional posting-date floor (SAP dd.mm.yyyy format) to keep the daily
    /// re-sync cheap once a vendor has years of history — omit it for a
    /// vendor's first-ever sync to pull everything.
    /// </summary>
    internal static RfcRequest BuildVendorGrRequest(string sapVendorNumber, string movementType, string? sinceDate = null)
    {
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

        builder
            .WhereCondition($"MSEG~WERKS EQ '{Plant}'")
            .WhereCondition($"MSEG~BWART EQ '{movementType}'")
            .WhereCondition("MSEG~SOBKZ EQ 'K'")
            .WhereCondition($"MSEG~LIFNR EQ '{vendor}'");

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
