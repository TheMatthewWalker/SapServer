using SapServer.Models;

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

    // MATNR, MBLNR, ZEILE, MENGE, MEINS, LIFNR (all MSEG), then BLDAT, BUDAT,
    // LFBNR (all MKPF) — LFBNR (vendor delivery note number) is the standard
    // SAP field for the vendor's own reference at goods receipt, and is what
    // the old workbooks' "Invoice Number" column (e.g. "RMIE 0041") was
    // always hand-typed from — it lives on the document header (MKPF), not
    // per line (MSEG), since one GR document can cover several materials off
    // the same delivery note.
    private static readonly string[] MsegColumns = ["MATNR", "MBLNR", "ZEILE", "MENGE", "MEINS", "LIFNR"];
    private static readonly string[] MkpfColumns  = ["BLDAT", "BUDAT", "LFBNR"];

    /// <summary>
    /// Pulls consignment goods-receipt lines (movement 101, SOBKZ=K) for one
    /// vendor. <paramref name="sapVendorNumber"/> should be the raw
    /// dbo.Vendor.SapVendorNumber value (padding is handled here, matching
    /// SapPad.Pad's idempotent-on-already-padded-values behaviour used
    /// everywhere else in this codebase). <paramref name="sinceDate"/> is an
    /// optional posting-date floor (SAP dd.mm.yyyy format) to keep the daily
    /// re-sync cheap once a vendor has years of history — omit it for a
    /// vendor's first-ever sync to pull everything.
    /// </summary>
    internal static RfcRequest BuildVendorGrRequest(string sapVendorNumber, string? sinceDate = null)
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
            .WhereCondition("MSEG~BWART EQ '101'")
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
            .Select(cols => new ConsignmentGrRow
            {
                Material         = PerformanceHelpers.NormaliseMaterial(cols[0]),
                MaterialDocument = cols[1],
                MaterialDocItem  = cols[2],
                Quantity         = decimal.TryParse(cols[3].Trim(), out var qty) ? qty : 0m,
                Uom              = cols[4],
                Vendor           = cols[5],
                DocumentDate     = cols[6],
                PostingDate      = cols[7],
                InvoiceNumber    = cols[8],
            })
            .ToArray();
    }
}
