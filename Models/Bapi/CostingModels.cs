namespace SapServer.Models.Bapi;

public sealed class CostSheetRequest
{
    /// <summary>Cost date in SAP format (e.g. "01.01.2026" or "20260101"). Maps to ZCOST_INFO3~KADAT.</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>Optional list of material numbers to filter by. If empty, all materials are returned.</summary>
    public List<string> Materials { get; init; } = [];
}


public sealed class ProfitCenterRequest
{
    /// <summary>Cost date in SAP format (e.g. "01.01.2026" or "20260101").</summary>
    public string DateFrom { get; init; } = string.Empty;
    public string DateTo { get; init; } = string.Empty;
    public string[] GlAccounts { get; init; } = [];
}

public sealed class FreightPostingRequest
{
    /// <summary>Cost date in SAP format (e.g. "01.01.2026" or "20260101").</summary>
    public string   DocDate    { get; init; } = string.Empty;
    public string   Vendor     { get; init; } = string.Empty;
    public decimal  Amount     { get; init; }
    public string   Currency   { get; init; } = string.Empty;
    public string   GlAccount  { get; init; } = string.Empty;
    public string   ProfitCenter { get; init; } = string.Empty;
    public string   Shipment   { get; init; } = string.Empty;
    public string   Information    { get; init; } = string.Empty;
}


public sealed class PeriodBalanceRequest
{
    public string FiscalYear { get; init; } = string.Empty; 
    public string PeriodFrom { get; init; } = string.Empty;
    public string PeriodTo { get; init; } = string.Empty;
    public string[] GlAccounts { get; init; } = [];
}


public class PeriodBalanceRow
{
    public string GlAccount { get; set; } = "";
    public string Period { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public decimal CumBalance { get; set; }
}


public class FreightPostingRow
{
    public string               AccountingNumber    { get; init; } = string.Empty;
    public bool                 Success             { get; init; }
    public List<SapReturnMessage> Messages          { get; init; } = [];
}



public sealed class ProfitCenterRow
{
                public string GlAccount { get; set; } = "";
                public string ProfitCenter { get; set; } = "";
                public string FiscalYear { get; set; } = "";
                public string PostingDate { get; set; } = "";
                public decimal CompanyCodeValue { get; set; }
                public string InvoiceNumber { get; set; } = "";
                public string InvoiceItem { get; set; } = "";
                public string MaterialNumber { get; set; } = "";
                public string Customer { get; set; } = "";
                public string SalesOrder { get; set; } = "";
                public string SalesOrderItem { get; set; } = "";
}



/// <summary>
/// A single row from the ZRFC_READ_TABLES multi-table join across
/// ZCOST_INFO3, PATN, and ZCOST_SHEET. Column order mirrors query_FIELDS registration.
/// </summary>
public sealed class CostSheetRow
{
    // ZCOST_INFO3 fields (cols 0–17)
    public string  Material      { get; init; } = string.Empty; // MATNR      [0]
    public string  Plant         { get; init; } = string.Empty; // WERKS      [1]
    public string  CostingDate   { get; init; } = string.Empty; // KADAT      [2]
    public string  ValidTo       { get; init; } = string.Empty; // BIDAT      [3]
    public string  ProfitCenter  { get; init; } = string.Empty; // PRCTR      [4]
    public string  CompanyCode   { get; init; } = string.Empty; // BUKRS      [5]
    public string  PartnerNumber { get; init; } = string.Empty; // PATNR      [6]
    public decimal Kst001        { get; init; }                  // KST001     [7]
    public decimal Kst008        { get; init; }                  // KST008     [8]
    public decimal Kst017        { get; init; }                  // KST017     [9]
    public decimal Kst002        { get; init; }                  // KST002     [10]
    public decimal Kst004        { get; init; }                  // KST004     [11]
    public decimal Kst019        { get; init; }                  // KST019     [12]
    public decimal Kst006        { get; init; }                  // KST006     [13]
    public decimal Kst033        { get; init; }                  // KST033     [14]
    public decimal LotSize       { get; init; }                  // LOSGR      [15]
    public string  Unit          { get; init; } = string.Empty;  // MEINS      [16]
    public string  Status        { get; init; } = string.Empty;  // FEH_STA   [17]

    // PATN fields (col 18)
    public string  Work          { get; init; } = string.Empty;  // WERK       [18]

    // ZCOST_SHEET fields (cols 19–22)
    public string  SheetValidFrom { get; init; } = string.Empty; // VALID_FROM [19]
    public string  SheetValidTo   { get; init; } = string.Empty; // VALID_TO   [20]
    public decimal OverheadPct    { get; init; }                  // OH_PCT     [21]
    public decimal IcMarkUp       { get; init; }                  // IC_MARK_UP [22]
}
