using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class FunctionHelperTests
{
    [Fact]
    public void BuildFunctionViewer_imports_the_function_name_and_reads_the_PARAMS_table()
    {
        var request = FunctionHelper.BuildFunctionViewer("BAPI_PO_CREATE1");
        Assert.Equal("BAPI_PO_CREATE1", request.ImportParameters["FUNCNAME"]);
        Assert.Equal(["PARAMETER", "PARAMCLASS", "TABNAME"], request.OutputTables["PARAMS"]);
    }

    [Fact]
    public void BuildFunctionFields_imports_the_structure_name()
    {
        var request = FunctionHelper.BuildFunctionFields("POHEADER");
        Assert.Equal("POHEADER", request.ImportParameters["TABNAME"]);
    }

    [Theory]
    [InlineData("I", "IMPORT")]
    [InlineData("E", "EXPORT")]
    [InlineData("T", "TABLE")]
    [InlineData("C", "CHANGING")]
    public void ParseFunctionViewer_translates_the_SAP_PARAMCLASS_code_to_a_readable_direction(string code, string expected)
    {
        var response = new RfcResponse
        {
            Tables = new() { ["PARAMS"] = [new() { ["PARAMETER"] = "VENDOR", ["PARAMCLASS"] = code, ["TABNAME"] = "" }] },
        };
        var result = FunctionHelper.ParseFunctionViewer(response);
        Assert.Equal(expected, result[0].Direction);
    }

    [Fact]
    public void ParseFunctionViewer_passes_through_an_unrecognised_PARAMCLASS_code_unchanged()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["PARAMS"] = [new() { ["PARAMETER"] = "X", ["PARAMCLASS"] = "Z", ["TABNAME"] = "" }] },
        };
        var result = FunctionHelper.ParseFunctionViewer(response);
        Assert.Equal("Z", result[0].Direction);
    }

    [Fact]
    public void ParseFunctionViewer_deduplicates_by_parameter_name_case_insensitively_keeping_the_first()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["PARAMS"] = [
                    new() { ["PARAMETER"] = "VENDOR", ["PARAMCLASS"] = "I", ["TABNAME"] = "" },
                    new() { ["PARAMETER"] = "vendor", ["PARAMCLASS"] = "E", ["TABNAME"] = "" }, // same name, different case
                ]
            },
        };
        var result = FunctionHelper.ParseFunctionViewer(response);
        Assert.Single(result);
        Assert.Equal("IMPORT", result[0].Direction); // the first occurrence wins
    }

    [Fact]
    public void ParseFunctionViewer_returns_empty_when_PARAMS_is_missing()
    {
        Assert.Empty(FunctionHelper.ParseFunctionViewer(new RfcResponse()));
    }

    [Fact]
    public void ParseFunctionFields_maps_field_name_type_and_length()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["DFIES_TAB"] = [new() { ["FIELDNAME"] = "VENDOR", ["DATATYPE"] = "CHAR", ["LENG"] = "000010" }] },
        };
        var result = FunctionHelper.ParseFunctionFields(response);
        Assert.Single(result);
        Assert.Equal("VENDOR", result[0].FieldName);
        Assert.Equal("CHAR", result[0].FieldType);
        Assert.Equal("000010", result[0].Length);
    }
}
