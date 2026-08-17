namespace SapServer.Models.Bapi;

/// <summary>
/// One material's total consumption (MVER goods-issue quantity, summed across all 12
/// periods) for one SAP fiscal year. See PerformanceHelpers.ParseConsumptionHistoryByYear —
/// reuses the same MVER pull BuildConsumptionHistoryRequest already makes for the rolling
/// 36-month history, just totalled per year instead of windowed.
/// </summary>
public sealed class ConsumptionByYearRow
{
    /// <summary>MATNR, leading zeros stripped (PerformanceHelpers.NormaliseMaterial).</summary>
    public string Material { get; init; } = string.Empty;

    /// <summary>SAP's fiscal year (GJAHR) — not necessarily the calendar year.</summary>
    public int FiscalYear { get; init; }

    /// <summary>Total consumption (goods issue quantity) for this material across the fiscal year.</summary>
    public decimal Qty { get; init; }
}

/// <summary>
/// One goods-receipt line (movement 101) straight off MSEG/MKPF — see
/// MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest. <see cref="Vendor"/> is read directly
/// off MSEG-LIFNR, which SAP only populates for vendor-linked stock categories (e.g.
/// consignment, SOBKZ='K') — blank here for an ordinary PO receipt, where the vendor has to
/// be resolved separately via the purchase order's own EKKO-LIFNR
/// (MrpAnalysisHelper.AggregateGoodsReceiptHistory does that resolution).
/// </summary>
public sealed class GoodsReceiptHistoryRawRow
{
    public string Material { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string Uom { get; init; } = string.Empty;
    public string PurchaseOrder { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Calendar year parsed from MKPF-BUDAT (posting date) — a real calendar date, unlike ConsumptionByYearRow.FiscalYear.</summary>
    public int Year { get; init; }
}

/// <summary>One purchase order's vendor, from EKKO. See MrpAnalysisHelper.BuildPurchaseOrderVendorsRequest.</summary>
public sealed class PurchaseOrderVendorRow
{
    public string PurchaseOrder { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
}

/// <summary>
/// Goods-receipt quantity for one material, from one vendor, in one calendar year — the
/// output of MrpAnalysisHelper.AggregateGoodsReceiptHistory (raw MSEG/MKPF rows joined to
/// their PO's EKKO vendor where MSEG-LIFNR itself was blank, then summed).
/// </summary>
public sealed class GoodsReceiptHistoryRow
{
    public string Material { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public int Year { get; init; }
    public decimal Qty { get; init; }
    public string Uom { get; init; } = string.Empty;
}

// ── BOM explosion (sales forecast -> raw materials) ─────────────────────────────────

/// <summary>One finished good's forecast/required quantity to explode down to raw materials.</summary>
public sealed class BomExplosionItem
{
    public string Material { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
}

public sealed class BomExplosionRequest
{
    public List<BomExplosionItem> Items { get; init; } = [];
}

/// <summary>One raw material's total required quantity, summed across every finished good/BOM level that needed it.</summary>
public sealed class RawMaterialRequirement
{
    public string Material { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string Uom { get; init; } = string.Empty;
}

/// <summary>
/// Result of MrpAnalysisHelper.ExplodeBom. <see cref="Unresolved"/> lists any material whose
/// quantity is included in <see cref="RawMaterials"/> despite never actually being classified
/// at the raw-material profit centre — either a BOM cycle (the same material reappearing as
/// its own descendant) or the explosion hitting its depth limit before finishing. Both are
/// folded into the totals rather than silently dropped, but flagged here so the caller can see
/// something needs checking rather than trusting the number blindly.
/// </summary>
public sealed class BomExplosionResult
{
    public List<RawMaterialRequirement> RawMaterials { get; init; } = [];
    public List<string> Unresolved { get; init; } = [];
}
