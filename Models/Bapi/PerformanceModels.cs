namespace SapServer.Models;

// ── Stock (LQUA) ────────────────────────────────────────────────────────────
// One row per batch/bin in warehouse 312. AvailableQty feeds the FIFO
// allocation; TotalQty in a staging bin feeds the "already picked" match.
public sealed class PerformanceStockRow
{
    public string Material        { get; init; } = ""; // MATNR
    public string Batch           { get; init; } = ""; // CHARG
    public string StorageBin      { get; init; } = ""; // LGPLA
    public string StorageType      { get; init; } = ""; // LGTYP
    public decimal TotalQty       { get; init; }       // GESME
    public decimal AvailableQty   { get; init; }       // VERME
    public string StorageLocation { get; init; } = ""; // LGORT
    public string PackagingMaterial { get; init; } = ""; // PALL_MATNR — from ZPRODBATCH join
    public string ProfitCentre { get; set; } = ""; //PRCTR gained from MARC lookup.
}

// ── Agreements (Z_STOCK_REQ_LIST) ──────────────────────────────────────────
// One row per open requirement. ReferenceDocument holds whichever document is
// the current MRP element for that requirement — a sales order number while
// the order is open, a delivery number once it's been picked. That's also
// what the staging-bin match for PickedStockAllocated keys off.
// DockStockAllocated / PickedStockAllocated are NOT populated here — they're
// filled in by the Node service after the SAP pull, since that's where the
// FIFO waterfall runs (see the allocation step in the Node sync job).
public sealed class AgreementRow
{
    public string ProfitCentre      { get; init; } = ""; // PRCTR
    public string Plant             { get; init; } = ""; // WERKS
    public string Mid               { get; init; } = ""; // WRKST
    public string MrpController     { get; init; } = ""; // DISPO
    public string Material          { get; init; } = ""; // MATNR
    public string MaterialText      { get; init; } = ""; // MAKTX
    public string ValueStream       { get; init; } = ""; // MDV01 — "WC" column in the workbook
    public decimal OnHandQty        { get; init; }       // summed from MATNR_INV where SRC02 = 'Unrestr'
    public string Uom               { get; init; } = ""; // MEINS
    public decimal StandardPrice    { get; init; }       // STPRS
    public string LocalCurrency     { get; init; } = ""; // WAERS on the material master
    public string Customer          { get; init; } = ""; // KUNNR
    public string CustomerGroup     { get; init; } = ""; // KONZS
    public string CustomerName      { get; init; } = ""; // NAME1
    public string OrderType         { get; init; } = ""; // AUART
    public string ReferenceDocument { get; init; } = ""; // VBELN — order OR delivery, see class comment
    public string Item              { get; init; } = ""; // POSNR
    public string CustomerPo        { get; init; } = ""; // BSTNK
    public string CustomerMaterial  { get; init; } = ""; // KDMAT
    public string CustomerReference { get; init; } = ""; // KNREF
    public string UnloadingPoint    { get; init; } = ""; // ABLAD
    public DateTime RequestDate     { get; init; }      // DATUM
    public string Week              { get; init; } = "";
    public string Period            { get; init; } = "";
    public decimal OrderQty         { get; init; }       // QTY
    public decimal Amount           { get; init; }       // QTY * (NETPR / KPEIN), document currency
    public string Currency          { get; init; } = ""; // WAERK
    public decimal LocalAmount      { get; set; }       // QTY * (NETPR / KPEIN), document currency

    // Filled in by Node after the pull — not part of the raw SAP response
    public decimal DockStockAllocated   { get; set; }
    public decimal PickedStockAllocated { get; set; }
}

// ── Invoicing (Z_SALE_ANAL_HIST) ───────────────────────────────────────────
public sealed class InvoiceRow
{
    public string Plant           { get; init; } = ""; // WERKS
    public string SalesOrg        { get; init; } = ""; // VKORG
    public DateTime InvoiceDate   { get; init; }        // FKDAT
    public string InvoiceType     { get; init; } = "";  // FKART
    public string InvoiceNumber   { get; init; } = "";  // VBELN
    public string DeliveryNote    { get; init; } = "";  // VGBEL
    public string SalesAgreement  { get; init; } = "";  // AUBEL
    public string SalesItem       { get; init; } = "";  // AUPOS
    public string CustomerPo      { get; init; } = "";  // BSTKD
    public string CustomerGroup   { get; init; } = "";  // KONZS
    public string Customer        { get; init; } = "";  // KUNAG
    public string Material        { get; init; } = "";  // MATNR
    public string MaterialText    { get; init; } = "";  // ARKTX
    public decimal Quantity       { get; init; }        // FKIMG
    public decimal DocumentAmount { get; init; }        // FNETWR
    public decimal LocalAmount    { get; init; }        // LNETWR
    public string Currency        { get; init; } = "";  // WAERK
    public string ProfitCentre    { get; init; } = "";  // PRCTR
    public string Period          { get; init; } = "";
}

public sealed class ProfitCentreRow
{
    public string Material      { get; init; } = ""; // MATNR
    public string ProfitCentre             { get; init; } = ""; // PRCTR
}

// ── OTIF (Z_CUST_INDEX_ANALYSE) ─────────────────────────────────────────────
public sealed class OtifRow
{
    public string Customer       { get; init; } = ""; // KUNNR
    public string CustomerName   { get; init; } = ""; // NAME1
    public string Plant          { get; init; } = ""; // WERKS
    public string ProfitCentre   { get; init; } = ""; // PRCTR
    public string Material       { get; init; } = ""; // MATNR
    public string MaterialText   { get; init; } = ""; // MAKTX
    public string Delivery       { get; init; } = ""; // VBELN
    public DateTime DeliveryDate { get; init; }        // LFDAT
    public decimal DeliveryQty   { get; init; }        // MENGE
    public string Uom            { get; init; } = "";  // MEINS
    public DateTime TargetDate   { get; init; }        // TARG1_DT
    public decimal TargetQty     { get; init; }        // TARG1_ORIG
    public string QtyClass       { get; init; } = "";  // QTYCLASS — "Q-" means short-shipped
    public string DateClass      { get; init; } = "";  // DATCLASS — "D+" means late

    // Mirrors the workbook's OTIF_basis formula: =IF(W2="D+",0,1)
    public bool OnTime => DateClass != "D+";
}

// ── MM Turns / Valuation Class (mm_turns_valclass.xlsm) ─────────────────────
// Direct port of the workbook's "get_all" report: material master + valuation
// (Get_marc_mara_makt_mbew), a 13-month rolling demand forecast built from
// Z_STOCK_REQ_LIST in summary mode (main_get_data), a 13-month rolling
// consumption history from MVER (Get_mver), last movement dates from S032
// (Get_s032), and the derived stock-turns / book-value / warning columns
// (recalc_turns / calc_book_value / make_warnings).
//
// Design deviation from the workbook: the VBA drives the final material list
// from the intersection of the demand-forecast call (Z_STOCK_REQ_LIST, which
// only ever returns FERT/HALB unless told otherwise) and the master-data call,
// deleting rows that don't match. This API instead treats the master-data
// call as authoritative (it's the one the plant/profit-centre/valuation-class
// filters apply to most naturally) and left-joins forecast/history/movement
// onto it — a material with stock but zero forecast still shows up with
// "No requirement" rather than being silently dropped, which is more useful
// for the review this report exists to support.

/// <summary>Query filters for GET /api/performance/turns-valclass. All list filters are optional (empty = no filter, matching the workbook's blank dialog defaults).</summary>
public sealed class TurnsValClassQuery
{
    public string?   Plant           { get; init; }              // defaults to PerformanceHelpers.Plant
    public string[]? ProfitCentres   { get; init; }               // PID
    public string[]? Materials       { get; init; }
    public string[]? MrpControllers  { get; init; }               // DISPO
    public string[]? MaterialTypes   { get; init; }               // MTART — blank in the workbook means "all types"
    public string[]? ValuationClasses{ get; init; }               // BKLAS — only applies to the master-data call; Z_STOCK_REQ_LIST has no BKLAS selection

    /// <summary>Number of months used to calculate turns/days-in-stock (workbook "turn_months", 1–12, default 4).</summary>
    public int TurnMonths { get; init; } = 4;

    /// <summary>
    /// false (default) = forward-looking: turns are based on the demand forecast for the next TurnMonths months.
    /// true = backward-looking: turns are based on actual consumption for the last TurnMonths months.
    /// Mirrors the workbook's history_on/history_off toggle.
    /// </summary>
    public bool HistoryMode { get; init; }
}

/// <summary>One row per material — the flat report row. Forecast/history are 13-entry arrays,
/// index 0 = 12 months out (oldest for history, furthest-out for forecast), index 12 = the current
/// (partial) calendar month — both series share that terminal month, mirroring the workbook's own
/// column layout where "Hist P{n}" and "Req P{n}" for the current month sit adjacent to each other.</summary>
public sealed class TurnsValClassRow
{
    public string   Material               { get; init; } = ""; // MATNR
    public string   MaterialText           { get; init; } = ""; // MAKTX
    public DateTime? CreatedDate           { get; init; }       // ERSDA
    public string   MaterialType           { get; init; } = ""; // MTART
    public string   Uom                    { get; init; } = ""; // MEINS
    public string   Plant                  { get; init; } = ""; // WERKS
    public string   ProfitCentre           { get; init; } = ""; // PRCTR
    public bool     DeletionFlag           { get; init; }       // LVORM
    public string   AbcIndicator           { get; init; } = ""; // MAABC
    public string   PurchasingGroup        { get; init; } = ""; // EKGRP
    public string   MrpController          { get; init; } = ""; // DISPO
    public string   ValuationClass         { get; init; } = ""; // BKLAS
    public string   LotSizeProcedure       { get; init; } = ""; // DISLS
    public decimal  PlanningTimeFence       { get; init; }       // FXHOR
    public decimal  GrProcessingTime        { get; init; }       // WEBAZ
    public decimal  TotalReplenishmentTime  { get; init; }       // DZEIT
    public decimal  SafetyStock             { get; init; }       // EISBE
    public decimal  MinLotSize              { get; init; }       // BSTMI
    public decimal  MaxLotSize              { get; init; }       // BSTMA
    public decimal  FixedLotSize            { get; init; }       // BSTFE
    public decimal  RoundingValue           { get; init; }       // BSTRF
    public string   SpecialProcurementType  { get; init; } = ""; // SOBSL
    public decimal  PlannedDeliveryTime     { get; init; }       // PLIFZ

    public decimal  StockQty               { get; init; }       // MBEW-LBKUM
    public decimal  StockValue             { get; init; }       // MBEW-SALK3
    public decimal  UnitPrice              { get; init; }       // MBEW-STPRS / MBEW-PEINH
    public decimal  BookValue              { get; init; }       // StockValue * factor(ValuationClass) — calc_book_value

    // Vendor consignment stock (MKOL, SOBKZ='K', unrestricted-use only — MKOL-SLABS), summed
    // across storage location/batch. Deliberately NOT part of StockQty/StockValue/BookValue
    // above: consignment stock has no value yet from our accounting perspective (it's excluded
    // from MBEW by design), so it must never touch anything valuation-facing. It exists purely
    // so MRP planning (Node-side buildWeeklyStockForecast) can see the FULL physically-available
    // quantity — StockQty + ConsignmentQty — when it decides what needs to be ordered.
    public decimal  ConsignmentQty         { get; init; }       // MKOL-SLABS, SOBKZ='K'

    public decimal[] DemandForecast        { get; init; } = new decimal[13]; // Z_STOCK_REQ_LIST summary, 13 rolling months
    public decimal[] ConsumptionHistory    { get; init; } = new decimal[13]; // MVER GSV01-12, 13 rolling months (M-12..Current)

    // 36 rolling months (M-35..Current) of the same MVER data — not rendered directly, used
    // server-side (Node) to compute a seasonal-index weighted predicted-usage forecast, which
    // needs multiple years of same-calendar-month history to build a seasonal index from.
    public decimal[] ConsumptionHistory36  { get; init; } = new decimal[36];

    public DateTime? LastReceiptDate       { get; init; } // S032 LETZTZUG
    public DateTime? LastGoodsIssueDate    { get; init; } // S032 LETZTABG
    public DateTime? LastConsumptionDate   { get; init; } // S032 LETZTVER
    public DateTime? LastGoodsMovementDate { get; init; } // S032 LETZTBEW

    public decimal? StockTurns             { get; init; } // null when TurnoverCategory is a non-numeric state (No stock, Neg. stock, No req. ...)
    public decimal? DaysInStock            { get; init; }
    public decimal  DailyRequirementValue  { get; init; } // "Daily req. value"
    public string   TurnoverCategory       { get; init; } = ""; // bucketed days-in-stock, or a non-numeric state
    public string   Warning                { get; init; } = ""; // make_warnings comment
}

/// <summary>Valid valuation classes for material types ROH/HALB/FERT/HIBE/VERP — T025/T025T/T134 join (Get_T025_T025T_T134).</summary>
public sealed class ValClassRow
{
    public string ValuationClass { get; init; } = ""; // BKLAS
    public string AccountRef     { get; init; } = ""; // KKREF
    public string MaterialType   { get; init; } = ""; // MTART
    public string Description    { get; init; } = ""; // BKBEZ
}

// ── Valuation class change (update_val_class) ───────────────────────────────
// Executes real SAP transactions: moves stock out to `Order` (MB1A 291/292),
// changes the valuation class via MM02, then moves stock back. This mutates
// SAP data — unlike the rest of PerformanceController it is not a read-only
// gateway. See PerformanceHelpers for the step-by-step port of update_val_class.

public sealed class ValClassChangeItem
{
    public string Material          { get; init; } = "";
    public string NewValuationClass { get; init; } = "";
}

public sealed class ChangeValuationClassRequest
{
    /// <summary>Production/CO order that stock is temporarily moved in and out of while MM02 runs.</summary>
    public string Order { get; init; } = "";
    public string? Plant { get; init; }
    public List<ValClassChangeItem> Changes { get; init; } = [];
}

public sealed class ValClassChangeResult
{
    public string  Material          { get; init; } = "";
    public string  MaterialText      { get; init; } = "";
    public string  Plant             { get; init; } = "";
    public decimal StockQty          { get; init; }
    public string  OldValuationClass { get; init; } = "";
    public string  NewValuationClass { get; init; } = "";
    public decimal OldBookValue      { get; init; }
    public decimal NewBookValue      { get; init; }
    public decimal ValueChange       { get; init; }
    public bool    Success           { get; init; }
    public string  Message           { get; init; } = ""; // MM02 BDC message (parsed)
}

public sealed class ChangeValuationClassResponse
{
    public bool    Success           { get; init; }
    public string? ErrorMessage      { get; init; } // set when the batch was aborted before any SAP writes happened
    public decimal TotalValueChange  { get; init; }
    public List<ValClassChangeResult> Results { get; init; } = [];
}

