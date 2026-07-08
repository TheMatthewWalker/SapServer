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
    public DateTime RequestDate     { get; init; }       // DATUM
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

