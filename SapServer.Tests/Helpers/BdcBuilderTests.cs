using SapServer.Helpers;

namespace SapServer.Tests.Helpers;

public class BdcBuilderTests
{
    [Fact]
    public void For_sets_the_transaction_code_and_defaults_to_synchronous_update_mode()
    {
        var request = BdcBuilder.For("MB1B").Build();
        Assert.Equal("Z_RFC_CALL_TRANSACTION", request.FunctionName);
        Assert.Equal("MB1B", request.ImportParameters["TRANCODE"]);
        Assert.Equal("S", request.ImportParameters["UPDMODE"]);
    }

    [Fact]
    public void For_accepts_an_explicit_update_mode()
    {
        var request = BdcBuilder.For("LT01", "A").Build();
        Assert.Equal("A", request.ImportParameters["UPDMODE"]);
    }

    [Fact]
    public void Screen_appends_a_DYNBEGIN_row_with_program_and_dynpro()
    {
        var request = BdcBuilder.For("MB1B").Screen("SAPMM07M", "0400").Build();
        var row = request.InputTablesItems["BDCTABLE"][0];
        Assert.Equal("SAPMM07M", row["PROGRAM"]);
        Assert.Equal("0400", row["DYNPRO"]);
        Assert.Equal("X", row["DYNBEGIN"]);
    }

    [Fact]
    public void Field_appends_an_FNAM_FVAL_row_preserving_call_order_across_screens()
    {
        var request = BdcBuilder.For("MB1B")
            .Screen("SAPMM07M", "0400")
                .Field("BDC_OKCODE", "/00")
                .Field("RM07M-WERKS", "3012")
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE", "=BU")
            .Build();

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Equal(5, rows.Count); // 2 screens + 3 fields, in call order
        Assert.Equal("0400", rows[0]["DYNPRO"]);
        Assert.Equal("/00", rows[1]["FVAL"]);
        Assert.Equal("3012", rows[2]["FVAL"]);
        Assert.Equal("0421", rows[3]["DYNPRO"]);
        Assert.Equal("=BU", rows[4]["FVAL"]);
    }

    /// <summary>
    /// FVAL (BDCDATA's field-value column) is always CHAR132 - confirmed for
    /// real against a live IIS deploy that passing a raw decimal/int through
    /// as object crashes with RfcTypeConversionException ("cannot convert
    /// Double into CHAR132"): SAP NCo's typed RfcDataContainer.SetValue has
    /// no auto-conversion from a numeric type into a CHAR field the way a
    /// COM VARIANT did. This test previously asserted the opposite (see git
    /// history) - it encoded exactly the wrong assumption that caused that
    /// crash, so it's corrected here rather than just having the assertion
    /// values swapped.
    /// </summary>
    [Fact]
    public void Field_overloads_stringify_decimal_and_int_values_for_the_CHAR132_FVAL_column()
    {
        var request = BdcBuilder.For("MB1B")
            .Field("MSEG-ERFMG(01)", 12.5m)
            .Field("SOME-INT-FIELD", 7)
            .Field("WHOLE-NUMBER-QTY", 15m)
            .Build();

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Equal("12.5", rows[0]["FVAL"]);
        Assert.Equal("7", rows[1]["FVAL"]);
        Assert.Equal("15", rows[2]["FVAL"]); // no trailing ".0"/".000000"
    }

    [Fact]
    public void FieldIf_only_appends_when_the_condition_is_true()
    {
        var request = BdcBuilder.For("MB1B")
            .FieldIf(false, "SKIPPED", "x")
            .FieldIf(true, "INCLUDED", "y")
            .Build();

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Single(rows);
        Assert.Equal("INCLUDED", rows[0]["FNAM"]);
    }

    [Fact]
    public void Build_registers_MESSG_as_a_5_field_structure_export_param()
    {
        var request = BdcBuilder.For("MB1B").Build();
        Assert.Equal(5, request.StructExportParameters["MESSG"]);
    }
}
