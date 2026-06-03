using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class FunctionHelper
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnTransaction = "Z_RFC_CALL_TRANSACTION";
    internal const string FnGetFunction = "RFC_GET_FUNCTION_INTERFACE";
    internal const string FnGetFunctionFields = "DDIF_FIELDINFO_GET";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";


    internal static RfcRequest BuildFunctionViewer(string functionName)
    {
        var builder = new RfcRequestBuilder(FnGetFunction)
            .Import("FUNCNAME", functionName)
            .ReadTable("PARAMS", "PARAMETER", "PARAMCLASS", "TABNAME");

        return builder.Build();
    }

    internal static RfcRequest BuildFunctionFields(string structureName)
    {
        var builder = new RfcRequestBuilder(FnGetFunctionFields)
            .Import("TABNAME", structureName)
            .ReadTable("DFIES_TAB", "FIELDNAME", "DATATYPE", "LENG");

        return builder.Build();
    }

    internal static FunctionParams[] ParseFunctionViewer(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("PARAMS", out var sapRows))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return sapRows
            .Where(row =>
            {
                var param = row.GetValueOrDefault("PARAMETER")?.ToString() ?? "";
                return seen.Add(param);
            })
            .Select(row => new FunctionParams
            {
                ParamName = row.GetValueOrDefault("PARAMETER")?.ToString() ?? "",
                Direction = ConvertDirection(
                    row.GetValueOrDefault("PARAMCLASS")?.ToString()),
                ParamType = row.GetValueOrDefault("TABNAME")?.ToString() ?? "",
                Fields = []
            })
            .ToArray();
    }


    internal static List<FunctionField> ParseFunctionFields(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("DFIES_TAB", out var rows))
            return [];

        return rows
            .Select(row => new FunctionField
            {
                FieldName = row.GetValueOrDefault("FIELDNAME")?.ToString() ?? "",
                FieldType = row.GetValueOrDefault("DATATYPE")?.ToString() ?? "",
                Length    = row.GetValueOrDefault("LENG")?.ToString() ?? ""
            })
            .ToList();
    }


    private static string ConvertDirection(string? value)
    {
        return value switch
        {
            "I" => "IMPORT",
            "E" => "EXPORT",
            "T" => "TABLE",
            "C" => "CHANGING",
            _   => value ?? string.Empty
        };
    }


// ── End ────────────────────────────────────────────────────────────────

}

