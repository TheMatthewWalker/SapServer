using SapServer.Models;
using System.Reflection;

namespace SapServer.Helpers;

public sealed class RfcRequestBuilder
{
    private readonly string                                                    _functionName;
    private readonly Dictionary<string, object?>                              _import         = new();
    private readonly Dictionary<string, Dictionary<string, object?>>          _structImport = new();
    private readonly Dictionary<string, List<Dictionary<string, object?>>>   _tables         = new();
    private readonly Dictionary<string, List<Dictionary<string, object?>>>   _tableItems     = new();
    private readonly List<string>                                             _export         = new();
    private readonly Dictionary<string, int>                                  _structExport   = new();
    private readonly Dictionary<string, List<string>>                        _outputTables   = new();
    private readonly WhereClauseBuilder                                       _where          = new();
    private bool _hasWhere;

    public RfcRequestBuilder(string functionName)
    {
        _functionName = functionName;
    }

    /// <summary>Adds a scalar import parameter (SAP EXPORTING).</summary>
    public RfcRequestBuilder Import(string key, object? value)
    {
        _import[key] = value;
        return this;
    }



    public RfcRequestBuilder StructImport(string name, object value)
    {
        _structImport[name] = ToDict(value);
        return this;
    }



    /// <summary>
    /// Appends a row to an input table accessed via func.Tables("name") → InputTables.
    /// <paramref name="row"/> may be an anonymous object or a Dictionary&lt;string, object?&gt;.
    /// </summary>
    public RfcRequestBuilder TableRow(string tableName, object row)
    {
        if (!_tables.TryGetValue(tableName, out var list))
            _tables[tableName] = list = [];
        list.Add(ToDict(row));
        return this;
    }

    /// <summary>
    /// Appends a row to an input table accessed via func.Tables.Item("name") → InputTablesItems.
    /// </summary>
    public RfcRequestBuilder TableItemRow(string tableName, object row)
    {
        if (!_tableItems.TryGetValue(tableName, out var list))
            _tableItems[tableName] = list = [];
        list.Add(ToDict(row));
        return this;
    }

    /// <summary>Appends a WHERE condition. Subsequent conditions are automatically prefixed with AND.</summary>
    public RfcRequestBuilder WhereCondition(string condition)
    {
        _where.Add(condition);
        _hasWhere = true;
        return this;
    }

    /// <summary>Adds a WHERE condition only when <paramref name="value"/> is not null or whitespace.</summary>
    public RfcRequestBuilder WhereConditionIf(string? value, Func<string, string> conditionFactory)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _where.Add(conditionFactory(value));
            _hasWhere = true;
        }
        return this;
    }

    /// <summary>Registers a scalar export parameter to read back from SAP (SAP IMPORTING).</summary>
    public RfcRequestBuilder ReadParam(string paramName)
    {
        _export.Add(paramName);
        return this;
    }

    /// <summary>
    /// Registers a structure export parameter to read back as a space-joined string.
    /// Reads <paramref name="fieldCount"/> positional fields — mirrors VB: x(1) &amp; " " &amp; x(2) &amp; ...
    /// </summary>
    public RfcRequestBuilder ReadStructParam(string paramName, int fieldCount)
    {
        _structExport[paramName] = fieldCount;
        return this;
    }

    /// <summary>
    /// Registers an output table to read back. Specify field names to extract individual columns.
    /// If no fields are provided, only the "WA" (work area) column is read — correct for ZRFC_READ_TABLES.
    /// </summary>
    public RfcRequestBuilder ReadTable(string tableName, params string[] fields)
    {
        _outputTables[tableName] = [.. fields];
        return this;
    }

    /// <summary>Builds the immutable <see cref="RfcRequest"/>.</summary>
    public RfcRequest Build()
    {
        var tableItems = new Dictionary<string, List<Dictionary<string, object?>>>(_tableItems);

        if (_hasWhere)
            tableItems["where_clause"] = _where.Build();

        return new RfcRequest
        {
            FunctionName           = _functionName,
            ImportParameters       = _import,
            StructImportParameters = _structImport,
            InputTables            = _tables,
            InputTablesItems       = tableItems,
            ExportParameters       = _export,
            StructExportParameters = _structExport,
            OutputTables           = _outputTables
        };
    }

    public RfcRequestBuilder Debug()
    {
        Console.WriteLine();
        Console.WriteLine("=== RFC REQUEST DEBUG ===");
        Console.WriteLine($"Function: {_functionName}");
        Console.WriteLine();

        // IMPORT PARAMETERS
        Console.WriteLine("== IMPORT PARAMETERS ==");
        if (_import.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var kv in _import)
                Console.WriteLine($"{kv.Key} = {kv.Value}");
        Console.WriteLine();

        // INPUT TABLES
        Console.WriteLine("== INPUT TABLES ==");
        if (_tables.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var (tableName, rows) in _tables)
                PrintTable(tableName, rows);
        Console.WriteLine();

        // INPUT TABLE ITEMS
        Console.WriteLine("== INPUT TABLE ITEMS ==");
        if (_tableItems.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var (tableName, rows) in _tableItems)
                PrintTable(tableName, rows);
        Console.WriteLine();

        // WHERE CLAUSE
        Console.WriteLine("== WHERE CLAUSE ==");
        if (_hasWhere)
        {
            var whereRows = _where.Build();
            PrintTable("WHERE", whereRows);
        }
        else
            Console.WriteLine("(none)");
        Console.WriteLine();

        // EXPORT PARAMETERS
        Console.WriteLine("== EXPORT PARAMETERS ==");
        if (_export.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var p in _export)
                Console.WriteLine(p);
        Console.WriteLine();

        // STRUCT EXPORT PARAMETERS
        Console.WriteLine("== STRUCT EXPORT PARAMETERS ==");
        if (_structExport.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var kv in _structExport)
                Console.WriteLine($"{kv.Key} (fields: {kv.Value})");
        Console.WriteLine();

        // OUTPUT TABLES
        Console.WriteLine("== OUTPUT TABLES ==");
        if (_outputTables.Count == 0)
            Console.WriteLine("(none)");
        else
            foreach (var (tableName, fields) in _outputTables)
                Console.WriteLine($"{tableName}: {(fields.Count == 0 ? "(WA only)" : string.Join(", ", fields))}");
        Console.WriteLine();

        Console.WriteLine("=== END RFC REQUEST DEBUG ===");
        Console.WriteLine();

        return this;
    }


    private static void PrintTable(string name, List<Dictionary<string, object?>> rows)
    {
        Console.WriteLine($"-- {name} --");

        if (rows.Count == 0)
        {
            Console.WriteLine("(empty)");
            return;
        }

        // Collect all column names
        var allColumns = rows
            .SelectMany(r => r.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Determine column widths
        var colWidths = allColumns.ToDictionary(
            col => col,
            col => Math.Max(
                col.Length,
                rows.Max(r => r.ContainsKey(col) && r[col] != null
                    ? r[col]!.ToString()!.Length
                    : 0)
            )
        );

        // Print header
        foreach (var col in allColumns)
            Console.Write($"{col.PadRight(colWidths[col] + 2)}");
        Console.WriteLine();

        // Separator
        foreach (var col in allColumns)
            Console.Write(new string('-', colWidths[col]) + "  ");
        Console.WriteLine();

        // Rows
        foreach (var row in rows)
        {
            foreach (var col in allColumns)
            {
                var value = row.ContainsKey(col) && row[col] != null
                    ? row[col]!.ToString()
                    : "";
                Console.Write(value.PadRight(colWidths[col] + 2));
            }
            Console.WriteLine();
        }
    }



    private static Dictionary<string, object?> ToDict(object obj)
    {
        if (obj is Dictionary<string, object?> d) return d;

        return obj.GetType()
                  .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                  .ToDictionary(p => p.Name, p => (object?)p.GetValue(obj));
    }
}
