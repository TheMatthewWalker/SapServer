namespace SapServer.Models.Bapi;

/// <summary>
/// Request for POST /api/purchasing/create-po-and-receipt — the combined,
/// per-user-elevated PO-creation-plus-goods-receipt flow (task #415).
///
/// Unlike POST /api/purchasing/create-po (which runs on the shared service
/// account pool), this endpoint logs into one of the pool's elevated worker
/// slots with SapUsername/SapPassword — one specific portal user's own SAP
/// credentials, decrypted just-in-time by the Node caller (see
/// lib/sapCredentials.js in the sql2005-bridge app) — creates the PO, commits
/// or rolls back, posts one goods receipt (MB01) per item, then logs the
/// elevated slot back out. All of that happens as one atomic unit of work on
/// one pinned SAP session; see PurchasingController.CreatePurchaseOrderAndReceipt.
///
/// Each <see cref="CreatePoAndReceiptItem"/> carries both its PO-item fields
/// (mirrors <see cref="PoCreateItem"/>) and its own goods-receipt fields
/// (mirrors <see cref="GoodsReceiptRequest"/> minus PurchaseOrder/LineNumber,
/// which the controller fills in once the PO number and item position are known)
/// — one PO item and one GR posting are always 1:1 here, matching the existing
/// one-MB01-call-per-cost-line design already used by
/// GET/POST-goods-receipt/GoodsReceiptHelper.
/// </summary>
public sealed class CreatePoAndReceiptRequest
{
    /// <summary>The calling user's own SAP username — used to log into an elevated worker slot for this request only.</summary>
    public string SapUsername { get; init; } = string.Empty;

    /// <summary>The calling user's own SAP password, decrypted just-in-time by the Node caller. Never logged, never persisted here.</summary>
    public string SapPassword { get; init; } = string.Empty;

    /// <summary>SAP vendor number. See PoCreateRequest.Vendor.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Document currency, e.g. "GBP". See PoCreateRequest.Currency.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Document date (dd.MM.yyyy or yyyyMMdd). Defaults to today if omitted.</summary>
    public string? DocDate { get; init; }

    public List<CreatePoAndReceiptItem> Items { get; init; } = [];
}

public sealed class CreatePoAndReceiptItem
{
    // ── PO item fields — mirrors PoCreateItem ──────────────────────────────
    public string? Material { get; init; }
    public string ShortText { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal NetPrice { get; init; }
    public string DeliveryDate { get; init; } = string.Empty;
    public string? AcctAssCat { get; init; }
    public string? MaterialGroup { get; init; }
    public string? GlAccount { get; init; }
    public string? CostCenterOrOrder { get; init; }

    // ── Goods-receipt fields for this same line — mirrors GoodsReceiptRequest
    //    (minus PurchaseOrder/LineNumber, filled in by the controller) ──────
    public string Reference { get; init; } = string.Empty;
    public string TrackingNumber { get; init; } = string.Empty;
    public string AddressCode { get; init; } = string.Empty;
    public string ShipmentCompletionDate { get; init; } = string.Empty;
    public string? PostingDate { get; init; }
}

/// <summary>Result of one line's goods-receipt posting within the combined flow.</summary>
public sealed class CreatePoAndReceiptLineResult
{
    /// <summary>1-based position on the PO, matching CreatePoAndReceiptRequest.Items order.</summary>
    public int LineNumber { get; init; }

    public bool Success { get; init; }

    /// <summary>Material document number posted by MB01, if successful. Store this per cost line for later individual reversal via /reverse-goods-receipt.</summary>
    public string? DocumentNumber { get; init; }

    /// <summary>Failure detail, if this line's goods receipt failed. The PO itself is still created/committed even if a line's GR fails — see the controller for why.</summary>
    public string? Error { get; init; }
}

public sealed class CreatePoAndReceiptResponse
{
    public string PurchaseOrder { get; init; } = string.Empty;
    public bool PoSuccess { get; init; }
    public List<SapReturnMessage> PoMessages { get; init; } = [];
    public List<CreatePoAndReceiptLineResult> Lines { get; init; } = [];
}
