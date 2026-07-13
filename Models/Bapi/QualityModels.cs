using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;


// ── QualityMb1b ──────────────────────────────────────────────────────────

public sealed class QualityMb1bRequest
{
    [Required, MinLength(1)] public string  Material        { get; init; } = string.Empty; 
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity        { get; init; }                 
    [Required, MinLength(1)] public string  Header          { get; init; } = string.Empty; 
                             public string  SpecialStockIndicator { get; init; } = string.Empty; 
                             public string  SpecialStockNumber { get; init; } = string.Empty; 
                             public string  Batch { get; init; } = string.Empty; 
    [Required, MinLength(1)] public string  StorageLocation { get; init; } = string.Empty; 
                             public string  BinType      { get; init; } = string.Empty; 
                             public string  Bin       { get; init; } = string.Empty; 
    [Required, MinLength(1)] public string  Username    { get; init; } = string.Empty; 
}

public sealed class QualityMb1bResponse
{
    public string Mb1bMessage           { get; init; } = string.Empty;
    public string ToNonBlockedMessage   { get; init; } = string.Empty;
    public string ToBlockedMessage      { get; init; } = string.Empty;
}
