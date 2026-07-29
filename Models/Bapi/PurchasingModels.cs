namespace SapServer.Models.Bapi;

/// <summary>
/// Request to create a Purchase Order via BAPI_PO_CREATE1, ported field-for-
/// field from the user's working Excel macro (AT_Dashboard.xlsm, module
/// BAPI_PO, sub CreateSAPPurchaseOrder) — that VBA is the ground truth this
/// was checked against, not generic SAP documentation.
///
/// Intended use (Inbound Log MIGO flow): one PoCreateRequest per shipment in
/// the unprocessed costs log, with one PoCreateItem per cost line on that
/// shipment. PO creation is step one of a two-step flow; the goods-receipt/
/// GRNI booking step is separate and not implemented here.
///
/// Also reused (unchanged) by the MRP order-suggestion "Create PO in SAP"
/// flow — see CreatePoElevatedModels.cs — for plain stock lines with no
/// account assignment. One deviation from the VBA source was needed there:
/// PoCreateItem.NetPrice is nullable so those orders can be posted without a
/// price and let SAP's own purchasing info record / condition determination
/// (ME12) fill it in, since the MRP vendor/material master data this feature
/// draws on doesn't store a price. See NetPrice's own comment below.
/// </summary>
public sealed class PoCreateRequest
{
    /// <summary>SAP vendor number. Padded to 10 chars (SapPad.Pad) before sending, matching the VBA's n2c(Range("po_vendor"), 10).</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Document currency, e.g. "GBP". Maps to POHEADER-CURRENCY.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Document date (dd.MM.yyyy or yyyyMMdd). Defaults to today if omitted, matching the VBA's Format(Now(),"YYYYMMDD").</summary>
    public string? DocDate { get; init; }

    public List<PoCreateItem> Items { get; init; } = [];
}

public sealed class PoCreateItem
{
    /// <summary>Material number — optional. Blank for non-stock/account-assigned lines (the VBA only sets MATERIAL "If Not inp(p,1) = \"\"").</summary>
    public string? Material { get; init; }

    /// <summary>Line description. Maps to POITEM-SHORT_TEXT.</summary>
    public string ShortText { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    /// <summary>Order unit, e.g. "EA". Maps to POITEM-PO_UNIT.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// Net price for the line, or null to let SAP determine it itself from
    /// the purchasing info record / condition records (ME12) during
    /// BAPI_PO_CREATE1 — this is standard SAP price determination, which
    /// only kicks in when NET_PRICE (and its POITEMX "X" flag) aren't sent
    /// at all. When a value IS supplied, PRICE_UNIT is always set to
    /// Quantity, matching the ported VBA exactly. Deliberately nullable
    /// rather than defaulting to 0 — an explicit 0 would still be sent to
    /// SAP as "the price is zero", not "work it out yourself".
    /// </summary>
    public decimal? NetPrice { get; init; }

    /// <summary>Schedule-line delivery date (dd.MM.yyyy or yyyyMMdd).</summary>
    public string DeliveryDate { get; init; } = string.Empty;

    /// <summary>Account assignment category: "K" = cost center, "F" = order, blank/null = standard stock line.</summary>
    public string? AcctAssCat { get; init; }

    /// <summary>Material group — only sent when AcctAssCat is set (matches the VBA's "If Not inp(p,7) = \"\" Then POitem(...,\"MATL_GROUP\")").</summary>
    public string? MaterialGroup { get; init; }

    /// <summary>GL account — only required/sent when AcctAssCat is set.</summary>
    public string? GlAccount { get; init; }

    /// <summary>Cost center (AcctAssCat == "K") or order number (AcctAssCat == "F"). Padded to 10 chars.</summary>
    public string? CostCenterOrOrder { get; init; }
}

public sealed class PoCreateRow
{
    public string PurchaseOrder { get; init; } = string.Empty;
    public bool   Success       { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
