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


/// <summary>A single material document row from the MSEG table.</summary>
public sealed class MsegRow
{
    public string  Material { get; init; } = string.Empty; // MATNR
    public string  StorageLocation     { get; init; } = string.Empty; // LGORT
    public decimal Quantity    { get; init; }                  // MENGE
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
