namespace SapServer.Models.Bapi;

/// <summary>
/// Stock adjustment via BAPI_GOODSMVT_CREATE, movement types 711 (stock
/// increase) / 712 (stock decrease) — posted directly against a bin without
/// a physical inventory document reference (mirrors transaction MB1C, "Other
/// Goods Receipts"). Requested to support a future 'Stock Investigation'
/// workflow: stock parked in the holding bin (999/TEMP) pending
/// investigation eventually needs writing off/correcting, and 711/712 is
/// the direct way to do that once the discrepancy is understood.
///
/// CAUTION — read before wiring this into Node: GoodsReceiptHelper.cs
/// already tried BAPI_GOODSMVT_CREATE once, for a different movement (101,
/// goods receipt against a PO via GM_CODE "01"), and per the user it did not
/// work against this SAP system — the working replacement there is a BDC
/// recording of transaction MB01 instead (see GoodsReceiptHelper's own
/// header comment). This is a second attempt at the same BAPI, but through
/// GM_CODE "06" (goods movements without reference — mirrors MB1C, which is
/// where 711/712 are normally entered), a materially different code path
/// inside the function module than GM_CODE "01". It is worth testing this
/// in isolation via test.http before assuming it fails the same way — that's
/// exactly why this request has both a dryRun mode and a real-call example.
/// If it turns out this GM_CODE also doesn't work here, the fallback is the
/// same pattern as GoodsReceiptHelper: ask the user for a working MB1C BDC
/// recording and rebuild this as a BdcBuilder call instead.
///
/// One request = one line item (one material/batch/bin being adjusted).
/// Field names below are ported directly from BAPI_GOODSMVT_CREATE's actual
/// parameter list (sapdatasheet.org/abap/func/bapi_goodsmvt_create.html),
/// not assumed from general SAP knowledge — every field/structure name was
/// checked against BAPI2017_GM_HEAD_01, BAPI2017_GM_CODE and
/// BAPI2017_GM_ITEM_CREATE's actual component lists before being used here.
/// </summary>
public sealed class StockAdjustmentRequest
{
    /// <summary>Material number. Maps to GOODSMVT_ITEM-MATERIAL (padded to 18).</summary>
    public string Material { get; init; } = string.Empty;

    /// <summary>Plant. Maps to GOODSMVT_ITEM-PLANT. Defaults to StockAdjustmentHelper.Plant ("3012") if not supplied.</summary>
    public string? Plant { get; init; }

    /// <summary>Storage location. Maps to GOODSMVT_ITEM-STGE_LOC.</summary>
    public string StorageLocation { get; init; } = string.Empty;
    /// <summary>
    /// WM storage type. Maps to GOODSMVT_ITEM-STGE_TYPE. Sent unconditionally
    /// alongside StorageBin below — CAUTION for the Stock Count feature's
    /// Production Count (storage location 1716, inventory-managed only, no
    /// WM/bin concept as far as LQUA is concerned): the user's own prior
    /// experience is that sending WM-shaped values here (storage type 'SA',
    /// bin 'PTFE') still posts successfully for an IM-only storage location,
    /// but this is UNVERIFIED against this SAP system — confirm via a
    /// TestRun=true call against a real 1716 material before Production
    /// Count posts anything for real. If it turns out SAP silently mis-posts
    /// or rejects it, StorageType/StorageBin may need to become optional
    /// here instead (omit GOODSMVT_ITEM-STGE_TYPE/STGE_BIN entirely for an
    /// IM-only posting) — test before assuming either way.
    /// </summary>
    public string StorageType { get; init; }  = string.Empty;
    public string StorageBin { get; init; }  = string.Empty;
    public string? StockCategory { get; init; }
    public string? SpecialStockIndicator { get; init; }
    public string? SpecialStockNumber { get; init; }

    /// <summary>Batch, if the material is batch-managed. Maps to GOODSMVT_ITEM-BATCH (padded to 10).</summary>
    public string? Batch { get; init; }

    /// <summary>
    /// Movement type — "711" (increase) / "712" (decrease) for ordinary
    /// unrestricted-use stock, or "717" (decrease) / "718" (increase) for
    /// stock category 'S' (blocked) — SAP won't post 711/712 against
    /// blocked stock. Maps to GOODSMVT_ITEM-MOVE_TYPE.
    /// </summary>
    public string MovementType { get; init; } = string.Empty;

    /// <summary>Quantity to adjust, always entered as a positive number — the movement type (711 vs 712) determines direction. Maps to GOODSMVT_ITEM-ENTRY_QNT.</summary>
    public decimal Quantity { get; init; }

    /// <summary>Unit of entry, e.g. "KG", "EA". Maps to GOODSMVT_ITEM-ENTRY_UOM.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>Valuation type, only needed for split-valuated materials. Maps to GOODSMVT_ITEM-VAL_TYPE.</summary>
    public string? ValuationType { get; init; }

    /// <summary>Reference document number/text, shown against the resulting material document. Maps to GOODSMVT_HEADER-REF_DOC_NO (max 16 chars).</summary>
    public string? Reference { get; init; }

    /// <summary>Posting date (dd.MM.yyyy or yyyyMMdd). Defaults to today. Maps to GOODSMVT_HEADER-PSTNG_DATE.</summary>
    public string? PostingDate { get; init; }

    /// <summary>Document date (dd.MM.yyyy or yyyyMMdd). Defaults to today. Maps to GOODSMVT_HEADER-DOC_DATE.</summary>
    public string? DocumentDate { get; init; }

    /// <summary>If true, asks SAP to simulate the posting (GOODSMVT_HEADER/TESTRUN "X") without actually creating a material document.</summary>
    public bool TestRun { get; init; }
}

public sealed class StockAdjustmentResponse
{
    public string MaterialDocument     { get; init; } = string.Empty;
    public string MaterialDocumentYear { get; init; } = string.Empty;
    public bool   Success              { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}
