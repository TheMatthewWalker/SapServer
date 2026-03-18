namespace SapServer.Models.Bapi;

// ── DisplayStock ─────────────────────────────────────────────────────────────

/// <summary>Optional filters for stock queries. Bound from [FromQuery] parameters.</summary>
public sealed class StockQuery
{
    public string? Material    { get; init; }
    public string? StorageType { get; init; }
    public string? Bin         { get; init; }
    public string? Batch       { get; init; }
    public int     RowCount    { get; init; } = 9999;
}

/// <summary>A single quant row from the LQUA table.</summary>
public sealed class StockRow
{
    public string  StorageLocation { get; init; } = string.Empty; // LGORT
    public string  StorageType     { get; init; } = string.Empty; // LGTYP
    public string  Bin             { get; init; } = string.Empty; // LGPLA
    public string  Material        { get; init; } = string.Empty; // MATNR
    public decimal AvailableQty    { get; init; }                  // VERME
    public string  Batch           { get; init; } = string.Empty; // CHARG
    public string  StockCategory   { get; init; } = string.Empty; // BESTQ
    public string  SpecialStockInd { get; init; } = string.Empty; // SOBKZ
    public string  SpecialStockNum { get; init; } = string.Empty; // SONUM
}

/// <summary>Total available quantity per material number.</summary>
public sealed class MaterialTotalRow
{
    public string  Material   { get; init; } = string.Empty;
    public decimal TotalQty   { get; init; }
    public int     QuantCount { get; init; }
}

/// <summary>Quant count and total quantity per storage type + bin.</summary>
public sealed class BinSummaryRow
{
    public string  StorageType { get; init; } = string.Empty;
    public string  Bin         { get; init; } = string.Empty;
    public int     QuantCount  { get; init; }
    public decimal TotalQty    { get; init; }
}

// ── CreateTransferOrder ──────────────────────────────────────────────────────

public sealed class CreateTransferOrderRequest
{
    // Required
    public string  StorageLocation    { get; init; } = string.Empty; // I_LGORT
    public string  Material           { get; init; } = string.Empty; // I_MATNR (padded to 18)
    public decimal Quantity           { get; init; }                  // I_ANFME
    public string  SourceBinType      { get; init; } = string.Empty; // I_VLTYP
    public string  SourceBin          { get; init; } = string.Empty; // I_VLPLA (padded to 10)
    public string  DestinationBinType { get; init; } = string.Empty; // I_NLTYP
    public string  DestinationBin     { get; init; } = string.Empty; // I_NLPLA (padded to 10)

    // Optional
    public string? Batch                 { get; init; }  // I_CHARG + I_ZEUGN (padded to 10)
    public string? StockCategory         { get; init; }  // I_BESTQ
    public string? SpecialStockIndicator { get; init; }  // I_SOBKZ
    public string? SpecialStockNumber    { get; init; }  // I_SONUM (padded to 16)
}

public sealed class CreateTransferOrderResponse
{
    public string               TransferOrderNumber { get; init; } = string.Empty;
    public bool                 Success             { get; init; }
    public List<SapReturnMessage> Messages          { get; init; } = [];
}

public sealed class SapReturnMessage
{
    public string Type    { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
