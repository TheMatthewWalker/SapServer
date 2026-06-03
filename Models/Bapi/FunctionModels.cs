using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

public sealed class FunctionQuery
{
    public string  FunctionName { get; init; } = string.Empty;
}


public sealed class FunctionParams
{
    public string               ParamName { get; init; } = string.Empty;
    public string               Direction { get; init; } = string.Empty;
    public string               ParamType { get; init; } = string.Empty;
    public List<FunctionField> Fields { get; set;} = [];
}


public sealed class FunctionField
{
    public string FieldName { get; init; } = "";
    public string FieldType { get; init; } = "";
    public string Length { get; init; } = "";
}



