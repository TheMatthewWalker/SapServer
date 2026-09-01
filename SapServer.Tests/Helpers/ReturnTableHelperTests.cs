using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class ReturnTableHelperTests
{
    [Fact]
    public void ExtractMessages_reads_TYPE_and_MESSAGE_directly_when_MESSAGE_is_populated()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "S", ["MESSAGE"] = "Posted OK" }] },
        };
        var result = ReturnTableHelper.ExtractMessages(response);

        Assert.Single(result);
        Assert.Equal("S", result[0].Type);
        Assert.Equal("Posted OK", result[0].Message);
    }

    // Confirmed live against BAPI_OUTB_DELIVERY_CHANGE: a real SAP rejection
    // came back with TYPE populated and MESSAGE blank, real text sitting in
    // MESSAGE_V1-V4 instead (a variable-substitution message with no static
    // text of its own — standard BAPIRET2 behavior).
    [Fact]
    public void ExtractMessages_falls_back_to_joined_MESSAGE_V1to4_when_MESSAGE_is_blank()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["RETURN"] = [new()
                {
                    ["TYPE"] = "E", ["MESSAGE"] = "",
                    ["MESSAGE_V1"] = "Delivery", ["MESSAGE_V2"] = "80001234",
                    ["MESSAGE_V3"] = "", ["MESSAGE_V4"] = "not found",
                }],
            },
        };
        var result = ReturnTableHelper.ExtractMessages(response);

        Assert.Equal("E", result[0].Type);
        Assert.Equal("Delivery 80001234 not found", result[0].Message);
    }

    [Fact]
    public void ExtractMessages_stays_blank_when_MESSAGE_and_every_present_variable_are_blank()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new()
            {
                ["TYPE"] = "S", ["MESSAGE"] = "",
                ["MESSAGE_V1"] = "", ["MESSAGE_V2"] = "", ["MESSAGE_V3"] = "", ["MESSAGE_V4"] = "",
            }] },
        };
        var result = ReturnTableHelper.ExtractMessages(response);
        Assert.Equal("", result[0].Message);
    }

    [Fact]
    public void ExtractMessages_is_unaffected_when_the_caller_never_requested_the_MESSAGE_V_columns_at_all()
    {
        // Most existing callers' ReadTable(...) only asks for TYPE/MESSAGE —
        // the MESSAGE_V1-4 keys simply aren't present in the row dictionary
        // at all in that case (not even as null), same shape as before this
        // fallback was added.
        var response = new RfcResponse
        {
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "S", ["MESSAGE"] = "" }] },
        };
        var result = ReturnTableHelper.ExtractMessages(response);
        Assert.Equal("", result[0].Message);
    }

    [Fact]
    public void ExtractMessages_returns_empty_list_when_the_table_is_absent()
    {
        Assert.Empty(ReturnTableHelper.ExtractMessages(new RfcResponse()));
    }

    [Fact]
    public void HasBlockingError_is_true_for_type_E_or_A_only()
    {
        Assert.True(ReturnTableHelper.HasBlockingError([new ReturnTableHelper.SapMessage("E", "x")]));
        Assert.True(ReturnTableHelper.HasBlockingError([new ReturnTableHelper.SapMessage("A", "x")]));
        Assert.False(ReturnTableHelper.HasBlockingError([new ReturnTableHelper.SapMessage("S", "x")]));
        Assert.False(ReturnTableHelper.HasBlockingError([new ReturnTableHelper.SapMessage("W", "x")]));
        Assert.False(ReturnTableHelper.HasBlockingError([]));
    }

    [Fact]
    public void GetParam_reads_a_scalar_export_parameter()
    {
        var response = new RfcResponse { Parameters = new() { ["RC"] = "0" } };
        Assert.Equal("0", ReturnTableHelper.GetParam(response, "RC"));
        Assert.Null(ReturnTableHelper.GetParam(response, "MISSING"));
    }
}
