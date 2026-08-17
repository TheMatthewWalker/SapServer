using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class ProductionHelpersTests
{
    [Fact]
    public void BuildBomRequest_filters_by_plant_and_upper_cased_padded_material()
    {
        var request = ProductionHelpers.BuildBomRequest(new BomQuery { Material = "30005r" });

        var whereRows = request.InputTablesItems["where_clause"];
        var whereClauses = string.Join(" ", whereRows.Select(r => r["TEXT"]));

        Assert.Contains("ZBOM_INFO~WERKS EQ '3012'", whereClauses);
        Assert.Contains("ZBOM_INFO~MATNR EQ '30005R'", whereClauses); // padding: mixed alnum, not all-digit, so no zero-padding — just upper-cased
    }

    [Fact]
    public void BuildBomRequest_pads_a_purely_numeric_material_with_leading_zeros()
    {
        var request = ProductionHelpers.BuildBomRequest(new BomQuery { Material = "12345678" });

        var whereRows = request.InputTablesItems["where_clause"];
        var whereClauses = string.Join(" ", whereRows.Select(r => r["TEXT"]));

        Assert.Contains("ZBOM_INFO~MATNR EQ '000000000012345678'", whereClauses);
    }

    [Fact]
    public void BuildBomRequest_omits_the_component_condition_when_none_given()
    {
        var request = ProductionHelpers.BuildBomRequest(new BomQuery { Material = "30005R" });
        var whereRows = request.InputTablesItems["where_clause"];

        Assert.DoesNotContain(whereRows, r => r["TEXT"]!.ToString()!.Contains("IDNRK"));
    }

    [Fact]
    public void ParseBomRows_parses_delimited_rows_into_typed_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MATNR|WERKS|IDNRK|POSNR|MENGE|MEINS|LGORT|PRVBE" }, // header — skipped
                    new() { ["WA"] = "30005R|3012|30006R|0010|2.5|KG|1710|312" },
                }
            }
        };

        var rows = ProductionHelpers.ParseBomRows(response);

        Assert.Single(rows);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal("30006R", rows[0].Component);
        Assert.Equal(2.5m, rows[0].ComponentQty);
        Assert.Equal("1710", rows[0].StorageLocation);
    }

    [Fact]
    public void ParseBomRows_drops_short_rows_instead_of_throwing()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header|only|has|four|cols" },
                    new() { ["WA"] = "too|short" },
                }
            }
        };

        var rows = ProductionHelpers.ParseBomRows(response);
        Assert.Empty(rows);
    }

    [Fact]
    public void ParseBomRows_returns_empty_when_the_table_is_missing_entirely()
    {
        var response = new RfcResponse();
        Assert.Empty(ProductionHelpers.ParseBomRows(response));
    }

    [Theory]
    [InlineData(null, "SD", "IB_363643_SD")]
    [InlineData("ACME", "SD", "IB_ACME_SD")]
    [InlineData("ACME", null, "")]
    [InlineData("ACME", "", "")]
    public void BuildPackagingInstruction_follows_the_no_customer_vs_no_packaging_rules(string? customer, string? packaging, string expected)
    {
        Assert.Equal(expected, ProductionHelpers.BuildPackagingInstruction(customer, packaging));
    }

    [Fact]
    public void ParseBdcResponse_extracts_type_class_number_message_and_document_number()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["MESSG"] = "S    ZF   040 Transfer order document 1234567 created" },
        };

        var result = ProductionHelpers.ParseBdcResponse(response);

        Assert.Equal("S", result.Type);
        Assert.Equal("ZF", result.MessageClass);
        Assert.Equal("040", result.MessageNumber);
        Assert.Equal("1234567", result.DocumentNumber);
        Assert.Contains("Transfer order document 1234567 created", result.Message);
    }

    [Fact]
    public void ParseBdcResponse_falls_back_to_the_raw_message_when_it_does_not_match_the_expected_shape()
    {
        // The type/class/number/message regex only needs 3 whitespace-separated
        // tokens followed by anything — a single word with no spaces at all is
        // the reliable non-matching case.
        var response = new RfcResponse { Parameters = new() { ["MESSG"] = "SystemFailure" } };

        var result = ProductionHelpers.ParseBdcResponse(response);

        Assert.Equal("", result.Type);
        Assert.Equal("SystemFailure", result.Message);
        Assert.Equal("", result.DocumentNumber);
    }

    [Fact]
    public void ParseSingleSapResult_throws_when_there_is_no_data_display_table()
    {
        var response = new RfcResponse();
        Assert.Throws<InvalidOperationException>(() => ProductionHelpers.ParseSingleSapResult(response));
    }

    [Fact]
    public void ParseSingleSapResult_returns_the_first_column_of_the_first_data_row()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = "1710" } } },
        };
        Assert.Equal("1710", ProductionHelpers.ParseSingleSapResult(response));
    }

    [Theory]
    [InlineData("X", true)]
    [InlineData("", false)]
    public void ParseRequiresCharge_treats_a_non_empty_flag_as_true(string flagValue, bool expected)
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = flagValue } } },
        };
        Assert.Equal(expected, ProductionHelpers.ParseRequiresCharge(response));
    }

    [Fact]
    public void ParseOrderText_joins_multiple_TDLINE_rows_trimmed()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["TEXT_LINES"] = new()
                {
                    new() { ["TDLINE"] = "First line of instructions.   " },
                    new() { ["TDLINE"] = "Second line.  " },
                    new() { ["TDLINE"] = "" }, // blank rows are dropped, not joined as empty lines
                },
            },
        };

        Assert.Equal("First line of instructions.\nSecond line.", ProductionHelpers.ParseOrderText(response));
    }

    [Fact]
    public void ParseOrderText_returns_empty_string_when_there_is_no_text()
    {
        Assert.Equal("", ProductionHelpers.ParseOrderText(new RfcResponse()));
    }

    // ── Bulk Profit Centre ──────────────────────────────────────────────────

    [Fact]
    public void BuildProfitCentresRequest_filters_by_plant_and_upper_cased_padded_materials()
    {
        var request = ProductionHelpers.BuildProfitCentresRequest(new ProfitCentresRequest { Materials = ["30005r", "12345678"] });

        var whereRows = request.InputTablesItems["where_clause"];
        var whereClauses = string.Join(" ", whereRows.Select(r => r["TEXT"]));
        Assert.Contains("MARC~WERKS EQ '3012'", whereClauses);
        Assert.Contains("MARC~MATNR IN opt", whereClauses);
    }

    [Fact]
    public void BuildProfitCentresRequest_writes_one_value_list_row_per_material_padded_and_upper_cased()
    {
        var request = ProductionHelpers.BuildProfitCentresRequest(new ProfitCentresRequest { Materials = ["30005r", "12345678"] });

        var rows = request.InputTablesItems["value_list"];
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (string?)r["LOW"] == "30005R");
        Assert.Contains(rows, r => (string?)r["LOW"] == "000000000012345678"); // 18-char zero-pad of the purely-numeric material
        Assert.All(rows, r =>
        {
            Assert.Equal("MARC", r["TABNAME"]);
            Assert.Equal("MATNR", r["FIELDNAME"]);
            Assert.Equal("I", r["SIGN"]);
            Assert.Equal("EQ", r["OPTION"]);
            Assert.Equal("", r["HIGH"]);
        });
    }

    [Fact]
    public void BuildProfitCentresRequest_is_not_rowcount_limited_unlike_the_single_material_variant()
    {
        var request = ProductionHelpers.BuildProfitCentresRequest(new ProfitCentresRequest { Materials = ["30005R"] });
        Assert.False(request.ImportParameters.ContainsKey("ROWCOUNT"));
    }

    [Fact]
    public void ParseProfitCentreRows_parses_delimited_material_and_profit_centre_pairs()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MATNR|PRCTR" }, // header — skipped
                    new() { ["WA"] = "30005R|0000002012" },
                    new() { ["WA"] = "30006R|0000002002" },
                }
            }
        };

        var rows = ProductionHelpers.ParseProfitCentreRows(response);

        Assert.Equal(2, rows.Length);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal("0000002012", rows[0].ProfitCentre);
        Assert.Equal("30006R", rows[1].Material);
        Assert.Equal("0000002002", rows[1].ProfitCentre);
    }

    [Fact]
    public void ParseProfitCentreRows_returns_empty_when_the_table_is_missing_entirely()
    {
        Assert.Empty(ProductionHelpers.ParseProfitCentreRows(new RfcResponse()));
    }
}
