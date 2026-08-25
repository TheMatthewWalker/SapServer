namespace SapServer.Models.Bapi;

/// <summary>
/// Posts Goods Issue for an outbound delivery via BAPI_DELIVERYPROCESSING_EXEC
/// — the BAPI behind VL06O's delivery-due-list background processing. Fired
/// automatically by Normanton-Nexus right after ZDELFLAG/ZDELPACK maintenance
/// succeeds for a delivery (see ZdelflagHelpers.cs) — there is no manual
/// approval step; GI is posted as soon as the packaging info is confirmed in
/// SAP.
///
/// CAUTION — read before touching REQUEST: this BAPI's REQUEST table
/// (BAPIDELICIOUSREQUEST) has ~200 fields and is normally populated by a
/// preceding due-list selection inside SAP itself, not hand-built by a
/// caller. The fields below are a deliberately minimal starting point (just
/// enough to identify the delivery and supply a date), confirmed only
/// against a live SE37 signature lookup for field *names/types* — not yet
/// confirmed as *sufficient* for SAP to accept the call. Expect to add more
/// REQUEST fields (DOCUMENT_ITEM/MATERIAL/PLANT/DOCUMENT_TYPE/
/// DOCUMENT_CATEGORY_SD/ITEM_TYPE, etc.) once a live test call's RETURN
/// messages say what's actually missing — do not pre-populate the rest of
/// REQUEST's ~200 fields blind. RETURN itself, by contrast, is a standard
/// BAPIRET2 table (same shape StockAdjustmentHelper/PurchasingHelper/
/// GoodsMovementHelper already parse successfully), so message-reading here
/// is low-risk, unlike ZDELFLAG's non-standard ET_MESSAGE.
///
/// Whether an explicit BAPI_TRANSACTION_COMMIT is required (as it is for
/// BAPI_GOODSMVT_CREATE — see StockAdjustmentModels.cs) is also unconfirmed
/// for this BAPI and must be verified live.
/// </summary>
public sealed class GoodsIssueRequest
{
    /// <summary>SAP delivery number (VBELN). Maps to DELIVERY_EXTEND-DELIVERY_NUMBER (padded to 10).</summary>
    public string DeliveryNumber { get; init; } = string.Empty;

    /// <summary>Maps to DELIVERY_EXTEND-NEW_DELIVERY_ALLOWED ("X"/""). Unconfirmed whether this needs to be set for a single-delivery (non-due-list) call — left off by default.</summary>
    public bool NewDeliveryAllowed { get; init; }

    /// <summary>
    /// Simulate only — maps to TECHN_CONTROL-CHECK_MODE ("X"/""). When true,
    /// the controller rolls back instead of committing, mirroring
    /// StockAdjustmentRequest.TestRun's dry-run-against-real-SAP convention.
    /// </summary>
    public bool CheckMode { get; init; }

    /// <summary>Maps to TECHN_CONTROL-DEBUG_FLG ("X"/""). Diagnostic aid only — leave off in normal use.</summary>
    public bool Debug { get; init; }

    /// <summary>Maps to REQUEST-DELIVERY_DATE (yyyyMMdd). Defaults to today when not supplied.</summary>
    public string? DeliveryDate { get; init; }

    /// <summary>Maps to REQUEST-GOODS_ISSUE_DATE (yyyyMMdd). Defaults to today when not supplied.</summary>
    public string? GoodsIssueDate { get; init; }

    /// <summary>
    /// True routes to the same CHECK_MODE-driven simulate/rollback path as
    /// CheckMode — kept as a separate app-level flag (mirroring
    /// StockAdjustmentRequest.TestRun) so callers can express "don't really
    /// post this" without needing to know CHECK_MODE is the SAP-side lever.
    /// </summary>
    public bool TestRun { get; init; }
}

public sealed class GoodsIssueResponse
{
    public string DeliveryNumber { get; init; } = string.Empty;
    public bool   Success        { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];

    /// <summary>
    /// Row count of the CREATEDITEMS output table — diagnostic-phase signal
    /// that SAP actually produced created items, without needing a temp log
    /// dump. Real shape/values are unconfirmed until at least one live
    /// successful call has been made.
    /// </summary>
    public int CreatedItemCount { get; init; }
}
