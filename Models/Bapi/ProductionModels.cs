using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

// ── Display Bom ─────────────────────────────────────────────────────────────

/// <summary>Optional filters for Bom queries. Bound from [FromQuery] parameters.</summary>
public sealed class BomQuery
{
    public string? Material    { get; init; }
    public string? Component { get; init; }
    public int     RowCount    { get; init; } = 9999;
}



/// <summary>A single quant row from the LQUA table.</summary>
public sealed class BomRow
{
    public string  Material { get; init; } = string.Empty; // MATNR
    public string  Plant     { get; init; } = string.Empty; // WERKS
    public string  Component             { get; init; } = string.Empty; // IDNRK
    public string  Item        { get; init; } = string.Empty; // POSNR
    public decimal ComponentQty    { get; init; }                  // MENGE
    public string  ComponentUnit           { get; init; } = string.Empty; // MEINS
    public string  StorageLocation   { get; init; } = string.Empty; // LGORT
    public string  SupplyArea { get; init; } = string.Empty; // PRVBE
}


public sealed class ProfitCentreRequest
{
    [ Required ] public string Material    { get; init; } = "";

}


public sealed class KgToUnitQuery
{
    public string Material    { get; init; } = string.Empty;
}


public sealed class KgToUnitRow
{
    public string  Material { get; init; } = string.Empty; // MATNR
    public decimal KgConversion    { get; init; }                  // MENGE
}



/// <summary>A single material document row from the MSEG table.</summary>
public sealed class MsegRow
{
    public string  Material { get; init; } = string.Empty; // MATNR
    public string  StorageLocation     { get; init; } = string.Empty; // LGORT
    public decimal Quantity    { get; init; }                  // MENGE
}


// ── Find Backflush Document (MSEG, movement 131) ──────────────────────────────
// Looks up the original backflush material document for a batch — used by the
// re-drum reversal chain to find what to reverse via MF41 before a
// batch-managed product is returned into stock.
public sealed class FindBackflushDocumentRequest
{
    [Required, MinLength(1)] public string Batch { get; init; } = string.Empty; // CHARG
}

/// <summary>The original 131 (backflush) movement for a batch, found via MSEG.</summary>
public sealed class BackflushDocumentRow
{
    public string  MaterialDocument { get; init; } = string.Empty; // MBLNR
    public string  Material         { get; init; } = string.Empty; // MATNR
    public decimal Quantity         { get; init; }                  // MENGE
    public string  StorageLocation  { get; init; } = string.Empty; // LGORT
}


// ── ZF40N Backflush ───────────────────────────────────────────────────
public sealed class Zf40nRequest
{
    [Required, MinLength(1)] public string  Material        { get; init; } = string.Empty; // MATNR → ST_FLD1-MATNR
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity        { get; init; }                  // MENGE → ST_FLD1-ERFMG
    [Required, MinLength(1)] public string  Header          { get; init; } = string.Empty; // MKPF-BKTXT → ST_FLD1-BKTXT
                             public string  Packaging       { get; init; } = string.Empty;
                             public string  Charge          { get; init; } = string.Empty; // CHARG → ST_FLD1-ACHARG
                             public string  Customer        { get; init; } = string.Empty;

}


// ── MF41 Reverse Backflush ───────────────────────────────────────────────────
public sealed class Mf41Request
{
    [Required, Length(10, 10)]  public string  MaterialDocument  { get; init; } = string.Empty; // MBLNR → RM07M-MBLNR
}



// ── MB11 Posting ───────────────────────────────────────────────────
public sealed class BomScrapRequest
{
    [Required, Length(1, 18)] public string  Material        { get; init; } = string.Empty;
                             public string  ComponentUnit        { get; init; } = string.Empty;
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity        { get; init; }
    [Required, MinLength(1)] public string  Header          { get; init; } = string.Empty;
    [Required, Length(3, 3)] public string  MovementType    { get; init; } = string.Empty;
    [Length(4, 4)]           public string  ScrapReason     { get; init; } = string.Empty;
                             public string StorageLocation { get; init; } = string.Empty;
                             public string ProfitCentre { get; init; } = string.Empty;
}


// ── BAPI_GOODSMVT_CREATE Posting — finished mix batch (not BOM components) ──
//
// For scrapping a whole expired mixing tub directly (movement 551), rather
// than looping over a material's BOM components the way
// PostScrap/BomScrapRequest does via MB11/BDC. Uses the same
// BAPI_GOODSMVT_CREATE / GM_CODE "06" path as StockAdjustmentHelper — see
// MixingScrapHelper.cs for the full rationale and the pinned-worker +
// explicit commit/rollback calling convention this BAPI requires. No
// batch/CHARG field: mix materials are not batch-managed in SAP — all
// tub/batch-level traceability for mixes lives in Normanton-Nexus only.
// StorageLocation is optional — the controller resolves it from MARC-LGPRO
// when not supplied, same as PostScrap already does per BOM component.
public sealed class MixingScrapRequest
{
    [Required, Length(1, 18)] public string  Material        { get; init; } = string.Empty;
                             public string? Plant             { get; init; }
                             public string? StorageLocation   { get; init; }
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity          { get; init; }
                             public string  Unit               { get; init; } = "KG";

    /// <summary>Reason for movement — GOODSMVT_ITEM-MOVE_REAS, 4-char SAP code.</summary>
    [Length(4, 4)]           public string? ScrapReason       { get; init; }

    /// <summary>Reference text on the resulting material document — GOODSMVT_HEADER-REF_DOC_NO (max 16 chars). The mix ref, for traceability inside SAP's own document text even though SAP itself carries no batch.</summary>
    [Required, MinLength(1)] public string  Header            { get; init; } = string.Empty;

    /// <summary>If true, asks SAP to simulate the posting (GOODSMVT_HEADER/TESTRUN "X") without creating a real material document.</summary>
                             public bool    TestRun           { get; init; }
}



// ── Default Bdc Response ───────────────────────────────────────────────────
public sealed class BdcResponse
{
    public string Type           { get; init; } = string.Empty;
    public string MessageClass   { get; init; } = string.Empty;
    public string MessageNumber  { get; init; } = string.Empty;
    public string Message        { get; init; } = string.Empty;
    public string DocumentNumber { get; init; } = string.Empty;
    public string RawMessage     { get; init; } = string.Empty;
}


public sealed class BdcWrapper
{
    public List<BdcResponse> Responses { get; init; } = [];
}


// ── Produced Batch Lookup (MSEG, movement 131) ────────────────────────────
// Finds the batch (CHARG) SAP assigned to the finished good on a just-posted
// backflush document — the inverse lookup of FindBackflushDocumentRequest
// above (that one goes CHARG -> MBLNR, this one goes MBLNR -> CHARG), same
// movement type. See BuildFindProducedBatchRequest for why 131 is correct.
public sealed class ProducedBatchRow
{
    public string  Charge   { get; init; } = string.Empty; // CHARG
    public string  Material { get; init; } = string.Empty; // MATNR
    public decimal Quantity { get; init; }                  // MENGE
}


// ── Concession Goods Movement (BAPI_GOODSMVT_CREATE) ──────────────────────
//
// Posts the ACTUAL components consumed by a job whose traceability was
// overridden by an approved Normanton-Nexus concession, instead of relying
// on ZF40N's automatic BOM-driven backflush (which would still consume
// whatever the BOM says, not what was really used). One call posts EVERY
// component explicitly — correct ones included, not just the substituted
// one — per Normanton-Nexus's "full replacement" design: this avoids
// ZF40N's own automatic backflush also silently consuming the original
// wrong BOM material on top of this explicit posting.
//
// UNCONFIRMED against this SAP system for this specific use case. Reuses
// GM_CODE "06" ("goods movements without reference") — the only GM_CODE
// confirmed working here so far (StockAdjustmentHelper/MixingScrapHelper,
// movements 711/551) — but the movement type below has NOT itself been
// exercised through this BAPI/GM_CODE combination before. Verify via
// test.http before trusting this in production; if it doesn't work, the
// established fallback in this codebase is a BDC recording instead (see
// GoodsReceiptHelper.cs's header comment for what that looked like the
// last time this exact BAPI failed here).
public sealed class GoodsMovementComponent
{
    [Required, Length(1, 18)] public string  Material        { get; init; } = string.Empty; // -> GOODSMVT_ITEM-MATERIAL
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity          { get; init; }                  // -> GOODSMVT_ITEM-ENTRY_QNT
                             public string  Unit               { get; init; } = string.Empty; // -> GOODSMVT_ITEM-ENTRY_UOM
                             public string? StorageLocation    { get; init; }                  // -> GOODSMVT_ITEM-STGE_LOC; controller resolves via MARC-LGPRO if blank (mirrors PostMixingScrap)
}

public sealed class GoodsMovementRequest
{
    /// <summary>The finished good this job produced — reference only (GOODSMVT_HEADER carries no MATNR itself), for logging/traceability.</summary>
    [Required, MinLength(1)] public string Material { get; init; } = string.Empty;

    /// <summary>Batch ref, shown against the resulting material document. Maps to GOODSMVT_HEADER-REF_DOC_NO (max 16 chars).</summary>
    [Required, MinLength(1)] public string Header    { get; init; } = string.Empty;

    [Required, MinLength(1)] public List<GoodsMovementComponent> Components { get; init; } = [];

    /// <summary>
    /// Movement type applied to every component line — GOODSMVT_ITEM-MOVE_TYPE.
    /// Suggested default "201" (goods issue, cost center) — UNCONFIRMED, see
    /// class header comment. Deliberately a request field, not a hardcoded
    /// constant, so a different value can be tried via test.http without a
    /// code change once the correct one for this use case is known.
    /// </summary>
    public string MovementType { get; init; } = "201";

    /// <summary>If true, asks SAP to simulate the posting (GOODSMVT_HEADER/TESTRUN "X") without creating a real material document.</summary>
    public bool TestRun { get; init; }
}

public sealed class GoodsMovementResponse
{
    public string MaterialDocument     { get; init; } = string.Empty;
    public string MaterialDocumentYear { get; init; } = string.Empty;
    public bool   Success              { get; init; }
    public List<SapReturnMessage> Messages { get; init; } = [];
}


// ── Combined Drumming Backflush + ZPRODBATCH/ZBATCHPACK Maintenance ──────────
// Drumming Entry's one point of difference from every other production
// process: a finished drum/box must also get a row in two custom SAP tables
// (ZPRODBATCH_TBL, ZBATCHPACK_TBL) recording its batch and outer packaging,
// via the Z_ZPRODBATCH_MAINT BAPI — see BuildProdBatchMaintRequest. This
// request bundles everything the combined endpoint needs to run the backflush,
// find the resulting batch, verify it against the material's BOM, and write
// the batch/pack rows, in one call from Node.
public sealed class DrumBackflushRequest
{
    [Required, MinLength(1)] public string Material { get; init; } = string.Empty; // MATNR
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                              public decimal Quantity { get; init; }                 // ERFMG
    [Required, MinLength(1)] public string Header    { get; init; } = string.Empty; // drum ref -> BKTXT
                              public string Customer  { get; init; } = string.Empty;
    [Required, MinLength(2)] public string PackCode   { get; init; } = string.Empty; // SD/MD/LD/XD/SB/MB/LB/XB/C1/C2
    [Range(0.001, double.MaxValue, ErrorMessage = "Weight must be greater than zero.")]
                              public decimal WeightKG  { get; init; }
    // Materials of the operator-linked traceability parent batches (Node's
    // prod.ProductionTrace, resolved to each parent's own Material before
    // this call — SapServer has no access to that table). Compared against
    // this material's production BOM to catch the wrong component being
    // traced against the wrong finished good. Empty = nothing to check.
                              public List<string> TraceabilityMaterials { get; init; } = [];
}

public sealed class DrumBackflushResponse
{
    public BdcResponse Backflush        { get; init; } = new();
    public string      MaterialDocument { get; init; } = string.Empty;
    public string      Batch            { get; init; } = string.Empty; // CHARG found post-backflush
    public string      RcBatch          { get; init; } = string.Empty; // Z_ZPRODBATCH_MAINT RC_BATCH
    public string      RcPack           { get; init; } = string.Empty; // Z_ZPRODBATCH_MAINT RC_PACK
    public bool         BomMismatch        { get; init; }
    public string[]     ExpectedComponents { get; init; } = []; // this material's BOM components (IDNRK)
    public string[]     ActualComponents   { get; init; } = []; // echoes request.TraceabilityMaterials
}
