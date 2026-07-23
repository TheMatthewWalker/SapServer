namespace SapServer.Models.Bapi;

/// <summary>
/// Request to post a goods receipt against a purchase order via transaction
/// MB01 (BDC recording), not a BAPI — BAPI_GOODSMVT_CREATE was tried first
/// but doesn't work against this SAP system, per the user. This mirrors
/// their own BDC recording exactly:
///
///   SAPMM07M 0200: BDC_OKCODE=/00, MKPF-BLDAT, MKPF-BUDAT, RM07M-LFSNR,
///                  MKPF-FRBNR, MKPF-BKTXT, RM07M-BWARTWE=101,
///                  RM07M-EBELN, RM07M-WERKS=3012, XFULL=X,
///                  RM07M-XNAPR=X, RM07M-WVERS1=X
///   SAPMM07M 0221 (once per PO item): BDC_CURSOR=MSEG-ERFMG(nn), BDC_OKCODE==SELE
///   SAPMM07M 0221 (final): BDC_OKCODE==BU
///
/// See GoodsReceiptHelper for the field-by-field build.
/// </summary>
public sealed class GoodsReceiptRequest
{
    /// <summary>PO number returned by PoCreateRow.PurchaseOrder. Maps to RM07M-EBELN.</summary>
    public string PurchaseOrder { get; init; } = string.Empty;

    /// <summary>
    /// Number of PO items to select (one MSEG-ERFMG(nn)/=SELE screen per
    /// item, nn = 01, 02, 03...). Must match how many PoCreateItem rows
    /// were sent when the PO was created.
    /// </summary>
    public int ItemCount { get; init; }

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
}

public sealed class GoodsReceiptResponse
{
    /// <summary>Raw SAP BDC result message (Z_RFC_CALL_TRANSACTION's MESSG).</summary>
    public string Message { get; init; } = string.Empty;
}
