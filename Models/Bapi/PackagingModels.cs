using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

// ── Engineering > Packaging Data ─────────────────────────────────────────
// DTOs for the three packaging tools ported from 363_new_packaging.xlsm:
// Mass Packaging Update, New Packaging Creation, Packaging Instruction Detail.

/// <summary>MARA fields shown as read-only context on the Dialog/detail screen.</summary>
public sealed class PackagingMaraRow
{
    public decimal WeightKg     { get; init; }
    public string  MaterialType { get; init; } = "";
    public string  HandlingType { get; init; } = "";
    public string  WeightUnit   { get; init; } = "";
}

/// <summary>A ZPACK_INSTR row — the packaging instruction assigned to a material,
/// either at plant-default level (Customer blank) or for a specific customer.</summary>
public sealed class PackagingInstrRow
{
    public string  PackMaterial { get; init; } = "";
    public decimal PalletQty    { get; init; }
    public decimal SmallBoxQty  { get; init; }
    public bool    PackProd     { get; init; }
    public bool    BoxGen       { get; init; }
    public bool    BatchSpread  { get; init; }
    public bool    PartMix      { get; init; }
    public bool    ChargeReq    { get; init; }
    public bool    TechStatReq  { get; init; }
    public bool    PNumReq      { get; init; }
}

/// <summary>A ZBOM_INFO component row for a packaging material.</summary>
public sealed class PackagingBomRow
{
    public string  Component { get; init; } = "";
    public string  Unit      { get; init; } = "";
    public decimal Quantity  { get; init; }
}

/// <summary>A customer this material is sold to, from ZKNA1_KNMT_V1.</summary>
public sealed class PackagingCustomerRow
{
    public string Customer       { get; init; } = "";
    public string CustomerGroup  { get; init; } = "";
    public string Name           { get; init; } = "";
    public string CustomerPart   { get; init; } = "";
    public string SalesOrg       { get; init; } = "";
}

/// <summary>Request body for reading/saving a single packaging instruction
/// (Packaging Instruction Detail / "Dialog" tool). Customer blank = plant default.</summary>
public sealed class PackagingInstrSaveRequest
{
    [Required, MinLength(1)] public string  Material     { get; init; } = "";
                              public string? Customer     { get; init; }
                              public string  SqlAction    { get; init; } = "U"; // "I" insert / "U" update / "D" delete
                              public string? PackMaterial { get; init; }
                              public decimal PalletQty    { get; init; }
                              public decimal SmallBoxQty  { get; init; }
                              public bool    PackProd     { get; init; }
                              public bool    BoxGen       { get; init; }
                              public bool    BatchSpread  { get; init; }
                              public bool    PartMix      { get; init; }
                              public bool    ChargeReq    { get; init; }   // "trace charge Id" — plant-level only
                              public bool    TechStatReq  { get; init; }
                              public bool    PNumReq      { get; init; }
}

/// <summary>Query params for a material/customer lookup — shared by several GET endpoints.</summary>
public sealed class PackagingLookupQuery
{
    [Required, MinLength(1)] public string  Material { get; init; } = "";
                              public string? Customer { get; init; }
}

/// <summary>Request body for the Mass Packaging Update tool — one row per material.</summary>
public sealed class MassPackagingUpdateRow
{
    [Required, MinLength(1)] public string Material     { get; init; } = "";
    [Required, MinLength(1)] public string PackMaterial { get; init; } = "";
}

public sealed class MassPackagingUpdateRequest
{
    [Required, MinLength(1)] public List<MassPackagingUpdateRow> Rows { get; init; } = [];
}

public sealed class MassPackagingUpdateResult
{
    public string  Material { get; init; } = "";
    public bool    Success  { get; init; }
    public string  Message  { get; init; } = "";
}

/// <summary>Request body for the New Packaging Creation tool.</summary>
public sealed class CreatePackagingRequest
{
    [Required, MinLength(1)] public string       CustomerPart { get; init; } = "";
    // Subset of PackagingHelpers.PackagingCodes. Empty/omitted = all 10 codes
    // (see PackagingController.Create), so this is intentionally not [Required].
    public List<string> Codes { get; init; } = [];
}

/// <summary>Result of creating one packaging-type code's material master + BOM.
/// (Assigning the resulting material as a material's packaging instruction is
/// a separate step, done via the Packaging Instruction Detail tool.)</summary>
public sealed class CreatePackagingResult
{
    public string Code            { get; init; } = "";
    public string Material        { get; init; } = "";
    public bool   AlreadyExisted  { get; init; }
    public bool   MaterialCreated { get; init; }
    public bool   BomCreated      { get; init; }
    public string Message         { get; init; } = "";
}
