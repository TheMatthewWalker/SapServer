using System.ComponentModel.DataAnnotations;

namespace SapServer.Models;

/// <summary>
/// Describes a single RFC function call. The caller specifies both what to send
/// and what to read back, so the worker can extract exactly the needed fields
/// without guessing at table structures.
/// </summary>
public sealed class RfcRequest
{
    /// <summary>SAP RFC function name (e.g. "L_TO_CREATE_SINGLE", "ZRFC_READ_TABLES").</summary>
    [Required, MinLength(1), MaxLength(100)]
    public string FunctionName { get; init; } = string.Empty;

    /// <summary>Scalar import parameters to pass to the RFC (SAP "EXPORTING" params).</summary>
    public Dictionary<string, object?> ImportParameters { get; init; } = new();

    /// <summary>Input table data to populate before calling the RFC (SAP "TABLES" with input rows).</summary>
    public Dictionary<string, List<Dictionary<string, object?>>> InputTables { get; init; } = new();
    public Dictionary<string, List<Dictionary<string, object?>>> InputTablesItems { get; init; } = new();

    /// <summary>
    /// Names of scalar export parameters to read from the RFC result (SAP "IMPORTING" params).
    /// </summary>
    public List<string> ExportParameters { get; init; } = new();

    /// <summary>
    /// Structure export parameters to read back as a concatenated string.
    /// Key = SAP parameter name; Value = number of positional fields to read and join.
    /// Mirrors the VB pattern: x(1) &amp; " " &amp; x(2) &amp; ... &amp; x(N).
    /// </summary>
    public Dictionary<string, int> StructExportParameters { get; init; } = new();

    /// <summary>
    /// Output tables to read from the RFC result.
    /// Key = SAP table name; Value = list of field names to extract per row.
    /// Pass an empty field list to read only the "WA" (work area) column,
    /// which is the pattern used by ZRFC_READ_TABLES.
    /// </summary>
    public Dictionary<string, List<string>> OutputTables { get; init; } = new();
}

/// <summary>The result of a successful RFC function call.</summary>
public sealed class RfcResponse
{
    /// <summary>Scalar values read from the RFC's IMPORTING parameters.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Rows read from the RFC's output tables, keyed by table name.</summary>
    public Dictionary<string, List<Dictionary<string, object?>>> Tables { get; init; } = new();
}

/// <summary>Health status snapshot of a single STA pool worker.</summary>
public sealed class WorkerStatus
{
    public int      SlotId       { get; init; }
    public bool     IsConnected  { get; init; }
    public int      QueueDepth   { get; init; }
    public DateTime LastActivity { get; init; }
}
