namespace SapServer.Models.Bapi;

/// <summary>
/// Request to post a goods receipt against a single purchase order item via
/// transaction MB01 (BDC recording), not a BAPI — BAPI_GOODSMVT_CREATE
/// doesn't work against this SAP system, per the user, who supplied the
/// working BDC recording this is built from.
///
/// One request = one PO item = one MB01 transaction. Per the user: even
/// though a shipment's costs all sit on one PO, each cost line/PO item gets
/// its own separate MB01 posting (not one BDC selecting multiple lines) —
/// this avoids the "select every line" complication when a PO spans more
/// than one page in the SAP GUI, and makes it possible to reverse a single
/// cost line later without touching the others. RM07M-EBELP identifies
/// which PO item is being received; RM07M-EBELN + RM07M-EBELP together
/// mean only that one line's quantity appears on the entry screen, so the
/// BDC only ever needs to select "line 1 of 1" (BDC_CURSOR=MSEG-ERFMG(01)),
/// never a variable-length loop.
///
/// See GoodsReceiptHelper for the field-by-field build. The response is
/// SapServer.Models.Bapi.BdcResponse (shared with the existing MF41/MBST
/// BDC endpoints in ProductionHelpers/ProductionController) — its
/// DocumentNumber field captures the posted material document number,
/// which the caller should store against this cost line so it can be
/// reversed individually later via POST /api/purchasing/reverse-goods-receipt.
/// </summary>
public sealed class GoodsReceiptRequest
{
    /// <summary>PO number returned by PoCreateRow.PurchaseOrder. Maps to RM07M-EBELN.</summary>
    public string PurchaseOrder { get; init; } = string.Empty;

    /// <summary>
    /// 1-based position of this cost line on the PO (1st item, 2nd item...).
    /// SAP renumbers PO items in 10s regardless of what was sent at
    /// creation (confirmed against a real test PO — BAPI_PO_CREATE1 ignores
    /// the sequential 00001/00002 values PurchasingHelper sends and assigns
    /// 10/20/30... itself), so GoodsReceiptHelper converts this to the
    /// actual SAP item number: LineNumber 1 -> EBELP "00010", 2 -> "00020", etc.
    /// </summary>
    public int LineNumber { get; init; }

    /// <summary>Reference of the cost (e.g. the Inbound Log shipment reference, "INB-000014"). Maps to RM07M-LFSNR.</summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>Tracking number of the shipment. Maps to MKPF-FRBNR.</summary>
    public string TrackingNumber { get; init; } = string.Empty;

    /// <summary>
    /// 4-character address code: 2-digit country code + 2-digit postcode
    /// (e.g. "US28"), built by the caller from the shipment's destination.
    /// Maps to MKPF-BKTXT.
    /// </summary>
    public string AddressCode { get; init; } = string.Empty;

    /// <summary>Shipment completion date (dd.MM.yyyy or yyyyMMdd). Maps to MKPF-BLDAT.</summary>
    public string ShipmentCompletionDate { get; init; } = string.Empty;

    /// <summary>Posting date (dd.MM.yyyy or yyyyMMdd) — always the date of posting. Defaults to today. Maps to MKPF-BUDAT.</summary>
    public string? PostingDate { get; init; }

    /// <summary>
    /// Confirmed received quantity for this PO item. Optional — when null or
    /// &lt;= 0, the request is built exactly as before: XFULL=X on screen
    /// 0200 has SAP default the entry screen to the full outstanding PO
    /// quantity, with no explicit override (the original, real-BDC-recording
    /// behaviour, unchanged). When supplied, GoodsReceiptHelper writes this
    /// value directly into MSEG-ERFMG(01) on screen 0221, overwriting
    /// whatever XFULL pre-filled, so a short/over delivery can be posted for
    /// exactly what arrived instead of the full PO line. Maps to MSEG-ERFMG(01).
    /// </summary>
    public decimal? Quantity { get; init; }
}
