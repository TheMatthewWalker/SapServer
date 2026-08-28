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
/// signature separates HEADER (DELIVERY/HEADER_DATA/HEADER_CONTROL, all
/// typed BAPIOBDLVHDRCHG/BAPIOBDLVHDRCTRLCHG — DELIVERY and HEADER_DATA are
/// two SEPARATE import parameters sharing the same structure type) from
/// ITEM (ITEM_DATA/ITEM_CONTROL tables, typed BAPIOBDLVITEMCHG/
/// BAPIOBDLVITEMCTRLCHG) — a normal "header + line items" change shape,
/// unlike BAPI_DELIVERYPROCESSING_EXEC's single flat ~200-field REQUEST
/// table. ITEM_CONTROL's CHG_DELQTY = "X" per item is what actually tells
/// SAP "apply the DLV_QTY from ITEM_DATA for this item" — mirrors
/// HEADER_CONTROL's per-field _FLG convention, standard across SAP
/// change-BAPIs.
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
///
/// LIVE DIAGNOSIS TIMELINE (2026-08-28, first real commit attempts,
/// delivery 0082291409/item 10/CP1442 — reducing SAP's LIPS-LFIMG from
/// 1297 EA to 1200 EA to match what was actually picked). Every stage
/// below was root-caused via a real T100 message-text lookup on the exact
/// TYPE/ID/NUMBER SAP returned, not guessed from the (often blank) MESSAGE
/// text alone — RETURN rows for this BAPI frequently come back with no
/// MESSAGE or MESSAGE_V1-4 at all, only TYPE/ID/NUMBER, so a temporary
/// diagnostic log of the raw RETURN row was used repeatedly to find those.
///
/// 1. DLV_QTY + MATERIAL + BASE_UOM alone: **VLBAPI 004 + VL 268**
///    ("quantity consistency check" / "Conversion factors 0:0 are zero,
///    not defined mathematically"), raised by SHP_QUANTITY_CONSISTENCY_CHECK.
///    **Fixed**: ITEM_DATA also needs DLV_QTY_IMUNIT, SALES_UNIT(+ISO),
///    BASE_UOM_ISO, and FACT_UNIT_NOM/FACT_UNIT_DENOM (LIPS-UMVKZ/UMVKN) —
///    none of these were being set at all. Confirmed via T006 that this
///    system's real ISO code for "EA" is "EA" (not "PCE"), and via a real
///    LIPS read that this delivery's UMVKZ/UMVKN are genuinely 1/1.
///    DeliveryChangeItem's SalesUnit/SalesUnitIso/BaseUomIso/FactUnitNom/
///    FactUnitDenom all default sensibly for the common same-unit case.
///    **Confirmed resolved** — this specific error stopped recurring.
/// 2. Next: **VL 302 "Delivery & does not exist"**, despite the delivery
///    genuinely existing (confirmed via picksheet-materials immediately
///    before/after). Tried and CONFIRMED WRONG: a plain "DELIVERY" struct
///    import parameter — despite being a real parameter per SAP's own BAPI
///    signature, NCo's GetStructure("DELIVERY") returned null and crashed
///    with a bare NullReferenceException (separately hardened in
///    NcoRfcExecutor.PopulateInputs to throw a clear SapExecutionException
///    instead — a real, permanent robustness fix regardless of this BAPI's
///    own outcome). **Fixed**: HEADER_DATA-DELIV_NUMB (the OTHER, correctly-
///    named BAPIOBDLVHDRCHG parameter) was never being set — added
///    alongside HEADER_CONTROL. **Confirmed resolved** — VL 302 stopped
///    recurring once HEADER_DATA-DELIV_NUMB was populated.
/// 3. Next, and still UNRESOLVED: **VL 019 "Picked quantity is larger than
///    the quantity to be delivered"**. Real LIPS-KCMENG (pick-confirmed
///    quantity) is confirmed 0 for this item — twice, independently — which
///    rules out the obvious "already partially picked" explanation. Tried
///    and did NOT change the outcome: also setting ITEM_DATA-CONV_FACT
///    (FLTP) to the same FACT_UNIT_NOM/DENOM ratio (1.0) — still VL 019,
///    identical every time. This looks like a genuine SAP business rule
///    (possibly: this delivery type doesn't support reducing DLV_QTY below
///    its original/created value via this BAPI at all, regardless of
///    picking status) rather than a missing request field — resolving it
///    needs either real SE37/ABAP debug access to see which internal check
///    raises this, or SAP functional/business input on whether this
///    delivery type genuinely supports this kind of reduction. CONV_FACT is
///    kept set (harmless, consistent with FACT_UNIT_NOM/DENOM) but is not
///    itself the fix.
///
/// Real SAP state after all of the above: completely unaffected — every
/// attempt was a TestRun that rolled back cleanly (confirmed via the
/// server log's BAPI_TRANSACTION_ROLLBACK ... OK line after every call).
/// Delivery 0082291409/item 10/CP1442 is still exactly 1297 EA.
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

    /// <summary>The new delivery quantity — what was actually picked. Maps to ITEM_DATA-DLV_QTY (sales unit).</summary>
    public decimal Quantity { get; init; }

    /// <summary>Base unit for Quantity (e.g. "KG", "EA"). Maps to ITEM_DATA-BASE_UOM.</summary>
    public string? BaseUom { get; init; }

    /// <summary>
    /// Sales unit for Quantity — maps to ITEM_DATA-SALES_UNIT. Confirmed live
    /// (2026-08-28) this is required: omitting SALES_UNIT/DLV_QTY_IMUNIT
    /// entirely made every real call fail with VLBAPI 004/VL 268 ("quantity
    /// consistency check", raised by the internal FM
    /// SHP_QUANTITY_CONSISTENCY_CHECK, which cross-validates DLV_QTY against
    /// DLV_QTY_IMUNIT via SALES_UNIT/BASE_UOM). Defaults to BaseUom when not
    /// given — correct whenever sales unit equals base unit (the common
    /// case, e.g. CP1442's LIPS-MEINS is "EA" with no separate sales unit on
    /// this delivery). If a material's real sales unit ever differs from its
    /// base unit, pass this explicitly — DLV_QTY_IMUNIT is derived as a
    /// straight copy of Quantity (see BuildDeliveryChangeRequest), which is
    /// only correct when the two units match 1:1.
    /// </summary>
    public string? SalesUnit { get; init; }

    /// <summary>
    /// ISO code for SalesUnit — maps to ITEM_DATA-SALES_UNIT_ISO. Defaults
    /// to SalesUnit/BaseUom's plain text when not given — confirmed correct
    /// for "EA" on this system via a real T006 read (ISOCODE = "EA", not
    /// "PCE"), but this is a per-unit T006 property, not a universal
    /// mapping; pass it explicitly for any other unit until confirmed.
    /// </summary>
    public string? SalesUnitIso { get; init; }

    /// <summary>ISO code for BaseUom — maps to ITEM_DATA-BASE_UOM_ISO. Same caveat as SalesUnitIso.</summary>
    public string? BaseUomIso { get; init; }

    /// <summary>
    /// Sales-to-base-unit conversion numerator — maps to ITEM_DATA-FACT_UNIT_NOM
    /// (LIPS-UMVKZ). Defaults to 1 when not given — correct whenever
    /// SalesUnit equals BaseUom (confirmed live: LIPS-UMVKZ/UMVKN for
    /// delivery 0082291409/item 10 are genuinely 1/1). Pass the real
    /// LIPS-UMVKZ value explicitly for a material whose sales unit differs
    /// from its base unit.
    /// </summary>
    public decimal? FactUnitNom { get; init; }

    /// <summary>Sales-to-base-unit conversion denominator — maps to ITEM_DATA-FACT_UNIT_DENOM (LIPS-UMVKN). Same default/caveat as FactUnitNom.</summary>
    public decimal? FactUnitDenom { get; init; }
}

public sealed class DeliveryChangeResponse
{
    public string DeliveryNumber { get; init; } = string.Empty;
    public bool   Success        { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
