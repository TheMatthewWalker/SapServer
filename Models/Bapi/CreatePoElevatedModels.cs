namespace SapServer.Models.Bapi;

/// <summary>
/// Request for POST /api/purchasing/create-po-elevated — PO creation only
/// (no goods receipt), run on a per-user elevated worker slot with the
/// calling user's own SAP credentials, exactly the same authorization
/// pattern as CreatePoAndReceiptRequest (see that file's header comment for
/// the full reasoning on why the shared service account can't be used here).
///
/// Built for the MRP order-suggestion "Create PO in SAP" flow (Nexus's
/// Tracked Orders page): unlike the Inbound Log MIGO flow that
/// create-po-and-receipt exists for, goods haven't arrived yet when an MRP
/// order is placed, so there's no receipt to post in the same call — this
/// stops after the PO is created and committed. See
/// PurchasingController.CreatePurchaseOrderElevated.
/// </summary>
public sealed class CreatePoElevatedRequest
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

    /// <summary>
    /// Plain PO items — reuses PoCreateItem as-is (including its nullable
    /// NetPrice) rather than a bespoke type, since none of the goods-receipt
    /// fields CreatePoAndReceiptItem carries alongside it are relevant here.
    /// </summary>
    public List<PoCreateItem> Items { get; init; } = [];
}

public sealed class CreatePoElevatedResponse
{
    public string PurchaseOrder { get; init; } = string.Empty;
    public bool   Success       { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
