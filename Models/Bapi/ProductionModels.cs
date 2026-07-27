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
