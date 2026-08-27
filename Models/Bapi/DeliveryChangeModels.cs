namespace SapServer.Models.Bapi;

/// <summary>
/// Changes an outbound delivery's item quantities (VL02N-equivalent) via
/// BAPI_OUTB_DELIVERY_CHANGE — used by Normanton-Nexus to bring SAP's own
/// delivery quantities (LIPS-LFIMG) in line with what was actually picked,
/// when the two are close (within 10%) but not exact, before ZDELFLAG/Goods
/// Issue can proceed (both require an exact match). See
/// routes/deliverymain.js's getDeliveryQuantityMatch/POST
/// /:deliveryId/sync-delivery-quantities on the Node side for the full flow.
///
/// CAUTION — read before touching the header structures: this BAPI's real
/// signature (from a live SE37 lookup — ground truth) separates HEADER
/// (DELIVERY/HEADER_DATA/HEADER_CONTROL, all typed BAPIOBDLVHDRCHG/
/// BAPIOBDLVHDRCTRLCHG) from ITEM (ITEM_DATA/ITEM_CONTROL tables, typed
/// BAPIOBDLVITEMCHG/BAPIOBDLVITEMCTRLCHG) — a normal "header + line items"
/// change shape, unlike BAPI_DELIVERYPROCESSING_EXEC's single flat ~200-
/// field REQUEST table. Since this feature only ever changes item
/// quantities, never header fields, DELIVERY/HEADER_DATA are left entirely
/// unset here — only HEADER_CONTROL-DELIV_NUMB is populated, on the
/// (UNCONFIRMED — verify live) assumption that SAP change-BAPIs generally
/// need the control structure's key field set to identify the target even
/// when none of that structure's own _FLG switches are set. ITEM_CONTROL's
/// CHG_DELQTY = "X" per item is what actually tells SAP "apply the DLV_QTY
/// from ITEM_DATA for this item" — mirrors HEADER_CONTROL's per-field _FLG
/// convention, standard across SAP change-BAPIs.
///
/// TECHN_CONTROL is left blank/default — no RECV_WHS_NO/RECV_SYS/DLV_TYPE
/// guess attempted. HEADER_CONTROL-SIMULATE's real semantics are
/// UNCONFIRMED and deliberately not relied on for a dry run — instead this
/// follows the same commit-or-rollback bracketing already proven for
/// BAPI_GOODSMVT_CREATE/BAPI_DELIVERYPROCESSING_EXEC (StockAdjustmentHelper/
/// GoodsIssueHelper): always call for real, then commit or roll back based
/// on TestRun. Whether ITEM_DATA-MATERIAL is actually required on a
/// CHG_DELQTY-only row is also UNCONFIRMED — included defensively since
/// some SAP change-BAPIs validate a row's MATERIAL still matches the
/// existing item even when it isn't itself being changed.
///
/// RETURN, by contrast, is a standard BAPIRET2 table — same low-risk shape
/// already proven via ReturnTableHelper for every other real BAPI in this
/// codebase, no guesswork needed there.
/// </summary>
public sealed class DeliveryChangeRequest
{
    /// <summary>SAP delivery number (VBELN). Maps to HEADER_CONTROL-DELIV_NUMB (padded to 10).</summary>
    public string DeliveryNumber { get; init; } = string.Empty;

    /// <summary>One entry per delivery item whose quantity needs correcting.</summary>
    public List<DeliveryChangeItem> Items { get; init; } = [];

    /// <summary>If true, the controller rolls back instead of committing — mirrors GoodsIssueRequest/StockAdjustmentRequest's TestRun convention.</summary>
    public bool TestRun { get; init; }
}

public sealed class DeliveryChangeItem
{
    /// <summary>SAP delivery item number (POSNR). Maps to ITEM_DATA/ITEM_CONTROL-DELIV_ITEM (padded to 6, numeric).</summary>
    public string ItemNumber { get; init; } = string.Empty;

    /// <summary>Material number, included defensively — see the class header caveat on whether SAP actually requires this for a quantity-only change. Maps to ITEM_DATA-MATERIAL.</summary>
    public string? Material { get; init; }

    /// <summary>The new delivery quantity — what was actually picked. Maps to ITEM_DATA-DLV_QTY.</summary>
    public decimal Quantity { get; init; }

    /// <summary>Base/sales unit for Quantity (e.g. "KG", "EA"). Maps to ITEM_DATA-BASE_UOM.</summary>
    public string? BaseUom { get; init; }
}

public sealed class DeliveryChangeResponse
{
    public string DeliveryNumber { get; init; } = string.Empty;
    public bool   Success        { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
