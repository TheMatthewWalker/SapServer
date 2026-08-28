namespace SapServer.Models.Bapi;

/// <summary>
/// Posts Goods Issue for an outbound delivery via BAPI_OUTB_DELIVERY_CONFIRM_DEC.
///
/// REPLACES an earlier attempt built on BAPI_DELIVERYPROCESSING_EXEC — that
/// BAPI's REQUEST table (BAPIDELICIOUSREQUEST, ~200 fields, normally
/// populated by a preceding due-list selection inside SAP itself) was never
/// confirmed working live: every real/CheckMode call this session returned
/// "The transferred sales document table is empty" + "Delivery not possible
/// at the moment", unchanged even after fixing the unrelated
/// BAPI_OUTB_DELIVERY_CHANGE issues and after batch-splitting the delivery
/// item. The user then shared the actual production ABAP program
/// (ZDELHAND_9) this business uses for real — it never calls
/// BAPI_DELIVERYPROCESSING_EXEC at all; Goods Issue there is posted via a
/// VL02N BDC's "=WABU_T" okcode, not a standalone BAPI. The user then
/// supplied sample code for BAPI_OUTB_DELIVERY_CONFIRM_DEC (setting
/// POST_GI_FLG on a HEADER_DATA typed BAPIOBDLVHDRCHG) — trying that live
/// threw a real NCo metadata error: "Element POST_GI_FLG of container
/// metadata BAPIOBDLVHDRCON unknown" (the real structure type is
/// BAPIOBDLVHDRCON, not BAPIOBDLVHDRCHG — a different, BAPI-specific type
/// despite the similar name). A follow-up real BAPI Inspector signature
/// confirmed POST_GI_FLG actually lives on HEADER_CONTROL
/// (BAPIOBDLVHDRCTRLCON), not HEADER_DATA — same header/control split
/// convention as BAPI_OUTB_DELIVERY_CHANGE, and HEADER_CONTROL's own
/// DELIV_NUMB is what actually serves as the key.
///
/// This BAPI has no SAP-side "check mode" relied on here (HEADER_CONTROL
/// does have its own SIMULATE field, same as BAPI_OUTB_DELIVERY_CHANGE's,
/// but its real semantics are unconfirmed) — TestRun below is this
/// codebase's own app-level dry-run convention: call for real, then commit
/// or roll back, same pattern already proven for every other real BAPI here
/// (BAPI_GOODSMVT_CREATE, BAPI_OUTB_DELIVERY_CHANGE).
/// </summary>
public sealed class GoodsIssueRequest
{
    /// <summary>SAP delivery number (VBELN). Maps to HEADER_CONTROL-DELIV_NUMB (padded to 10) — HEADER_DATA-DELIV_NUMB is also set defensively.</summary>
    public string DeliveryNumber { get; init; } = string.Empty;

    /// <summary>
    /// Per-item picking confirmation — maps to ITEM_DATA_SPL (/SPE/BAPIOBDLVITEMCONF)
    /// rows. Confirmed live (2026-08-28): POST_GI_FLG alone isn't enough —
    /// SAP rejected with "Delivery has not yet been put away / picked
    /// (completely)" for every item until QTY_POST was supplied here, one
    /// entry per real delivery item (including any batch-split sub-items —
    /// e.g. the auto-assigned 900001/900002/900003 SAP creates for a
    /// BAPI_OUTB_DELIVERY_CHANGE batch split, not the sub-item numbers
    /// originally requested).
    /// </summary>
    public List<GoodsIssueItem> Items { get; init; } = [];

    /// <summary>If true, the controller rolls back instead of committing — mirrors DeliveryChangeRequest/StockAdjustmentRequest's TestRun convention.</summary>
    public bool TestRun { get; init; }
}

public sealed class GoodsIssueItem
{
    /// <summary>Real SAP delivery item number (POSNR) — for a batch-split delivery this is the real sub-item SAP assigned (e.g. "900001"), not necessarily what was originally requested. Maps to ITEM_DATA_SPL-DELIV_ITEM (padded to 6, numeric).</summary>
    public string ItemNumber { get; init; } = string.Empty;

    /// <summary>Quantity being confirmed as picked for this item — maps to ITEM_DATA_SPL-QTY_POST.</summary>
    public decimal Quantity { get; init; }

    /// <summary>Unit for Quantity (e.g. "EA") — maps to ITEM_DATA_SPL-BASE_UOM.</summary>
    public string? BaseUom { get; init; }
}

public sealed class GoodsIssueResponse
{
    public string DeliveryNumber { get; init; } = string.Empty;
    public bool   Success        { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
