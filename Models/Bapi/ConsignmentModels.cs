namespace SapServer.Models.Bapi;

// ── Vendor Consignment Tracker ────────────────────────────────────────────────
//
// Backs GET /api/consignment/gr and GET /api/consignment/stock — see
// ConsignmentHelpers.cs for the full rationale (why GR + live stock only,
// not a raw consumption pull).

/// <summary>
/// One consignment goods-receipt line (MSEG BWART=101 — GR — or its reversal,
/// BWART=102/SOBKZ=K, joined to MKPF).
/// </summary>
public sealed class ConsignmentGrRow
{
    public string  Material         { get; init; } = string.Empty; // MSEG-MATNR
    public string  MaterialDocument { get; init; } = string.Empty; // MSEG-MBLNR
    public string  MaterialDocItem  { get; init; } = string.Empty; // MSEG-ZEILE
    // MSEG-MENGE, signed via MSEG-SHKZG: negative for a 102 reversal so
    // SUM(Quantity) nets to what SAP actually shows as delivered.
    public decimal Quantity         { get; init; }
    public string  Uom              { get; init; } = string.Empty; // MSEG-MEINS
    public string  Vendor           { get; init; } = string.Empty; // MSEG-LIFNR
    public string  DocumentDate     { get; init; } = string.Empty; // MKPF-BLDAT, dd.mm.yyyy
    public string  PostingDate      { get; init; } = string.Empty; // MKPF-BUDAT, dd.mm.yyyy
    public string  InvoiceNumber    { get; init; } = string.Empty; // MSEG-XBLNR_MKPF (invoice reference)
    // MSEG-SMBLN/SMBLP — "material document for reversal": populated ONLY on
    // the line that cancels another document (SAP transaction MBST), pointing
    // back at the document+item it reverses. Blank for an ordinary GR/reversal
    // that nothing has (yet) cancelled. See ConsignmentHelpers.cs's header
    // comment for why this needs to reach Normanton-Nexus at all — a cancelled
    // GR's own RemainingQty never got zeroed out downstream without it.
    public string  ReversalOfMaterialDocument { get; init; } = string.Empty; // MSEG-SMBLN
    public string  ReversalOfMaterialDocItem  { get; init; } = string.Empty; // MSEG-SMBLP
}
