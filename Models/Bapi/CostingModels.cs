namespace SapServer.Models.Bapi;

public sealed class CostSheetRequest
{
    /// <summary>Cost date in SAP format (e.g. "01.01.2026" or "20260101"). Maps to ZCOST_INFO3~KADAT.</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>Optional list of material numbers to filter by. If empty, all materials are returned.</summary>
    public List<string> Materials { get; init; } = [];
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
