using SapServer.Models;
using System.Reflection;

namespace SapServer.Helpers;

public sealed class RfcRequestBuilder
{
    private readonly string                                                    _functionName;
    private readonly Dictionary<string, object?>                              _import       = new();
    private readonly Dictionary<string, List<Dictionary<string, object?>>>   _tables       = new();
    private readonly Dictionary<string, List<Dictionary<string, object?>>>   _tableItems   = new();
    private readonly List<string>                                             _export       = new();
    private readonly Dictionary<string, List<string>>                        _outputTables = new();
    private readonly WhereClauseBuilder                                       _where        = new();
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
            FunctionName     = _functionName,
            ImportParameters = _import,
            InputTables      = _tables,
            InputTablesItems = tableItems,
            ExportParameters = _export,
            OutputTables     = _outputTables
        };
    }

    private static Dictionary<string, object?> ToDict(object obj)
    {
        if (obj is Dictionary<string, object?> d) return d;

        return obj.GetType()
                  .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                  .ToDictionary(p => p.Name, p => (object?)p.GetValue(obj));
    }
}
