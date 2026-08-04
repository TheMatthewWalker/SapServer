using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class CostingHelperTests
{
    [Fact]
    public void BuildCostSheetRequest_filters_by_plant_status_and_the_given_date()
    {
        var request = CostingHelper.BuildCostSheetRequest(new CostSheetRequest { Date = "15.03.2026" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("ZCOST_INFO3~WERKS EQ '3012'", whereText);
        Assert.Contains("ZCOST_INFO3~FEH_STA EQ 'FR'", whereText);
        Assert.Contains("ZCOST_INFO3~BIDAT EQ '15.03.2026'", whereText);
        Assert.Contains("ZCOST_SHEET~VALID_FROM EQ '01.01.2026'", whereText); // year derived from Date
    }

    [Fact]
    public void BuildCostSheetRequest_adds_an_IN_list_only_when_materials_are_given()
    {
        var withMaterials = CostingHelper.BuildCostSheetRequest(new CostSheetRequest { Date = "15.03.2026", Materials = ["30005R", "30006R"] });
        var whereText = string.Join(" ", withMaterials.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("ZCOST_INFO3~MATNR IN opt", whereText);
        Assert.Equal(2, withMaterials.InputTablesItems["value_list"].Count);

        var withoutMaterials = CostingHelper.BuildCostSheetRequest(new CostSheetRequest { Date = "15.03.2026" });
        Assert.False(withoutMaterials.InputTablesItems.ContainsKey("value_list"));
    }

    [Fact]
    public void ParseCostSheetRows_maps_delimited_columns_and_converts_European_decimals()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["DATA_display"] = new()
                {
                    new() { ["WA"] = "header;row;skipped;here;too;a;b;c;d;e;f;g;h;i;j;k;l;m;n;o;p;q;r" },
                    new() { ["WA"] = "30005R;3012;20260101;20261231;2000;0312;VEND1;1.234,56;0;0;0;0;0;0;0;100;KG;R;3012;01.01.2026;31.12.2026;10,5;2,25" },
                }
            }
        };

        var rows = CostingHelper.ParseCostSheetRows(response);

        Assert.Single(rows);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal(1234.56m, rows[0].Kst001);
        Assert.Equal("KG", rows[0].Unit);
        Assert.Equal(10.5m, rows[0].OverheadPct);
        Assert.Equal(2.25m, rows[0].IcMarkUp);
    }

    [Fact]
    public void BuildPeriodBalances_pads_the_GL_account_and_uses_currency_type_10()
    {
        var request = CostingHelper.BuildPeriodBalances(new PeriodBalanceRequest { FiscalYear = "2026" }, "600100");
        Assert.Equal("0000600100", request.ImportParameters["GLACCT"]);
        Assert.Equal("2026", request.ImportParameters["FISCALYEAR"]);
        Assert.Equal("10", request.ImportParameters["CURRENCYTYPE"]);
        Assert.Equal("0312", request.ImportParameters["COMPANYCODE"]);
    }

    [Fact]
    public void ParsePeriodBalances_filters_to_the_given_period_range_inclusive()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["ACCOUNT_BALANCES"] = new()
                {
                    new() { ["GL_ACCOUNT"] = "600100", ["FIS_PERIOD"] = "001", ["DEBITS_PER"] = "100,00", ["CREDIT_PER"] = "0,00", ["BALANCE"] = "100,00" },
                    new() { ["GL_ACCOUNT"] = "600100", ["FIS_PERIOD"] = "006", ["DEBITS_PER"] = "50,00", ["CREDIT_PER"] = "0,00", ["BALANCE"] = "50,00" },
                    new() { ["GL_ACCOUNT"] = "600100", ["FIS_PERIOD"] = "012", ["DEBITS_PER"] = "10,00", ["CREDIT_PER"] = "0,00", ["BALANCE"] = "10,00" },
                }
            }
        };

        var rows = CostingHelper.ParsePeriodBalances(response, "3", "9");

        Assert.Single(rows);
        Assert.Equal("006", rows[0].Period);
        Assert.Equal(50.00m, rows[0].Debit);
    }

    [Fact]
    public void ParsePeriodBalances_returns_empty_when_the_table_is_missing()
    {
        Assert.Empty(CostingHelper.ParsePeriodBalances(new RfcResponse(), "1", "12"));
    }

    [Fact]
    public void BuildProfitCenterRequest_derives_the_month_and_year_range_from_the_date_range()
    {
        var request = CostingHelper.BuildProfitCenterRequest(new ProfitCenterRequest { DateFrom = "01.03.2026", DateTo = "15.06.2026", GlAccounts = ["600100"] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("GLPCA~POPER BETWEEN '03' AND '06'", whereText);
        Assert.Contains("GLPCA~RYEAR BETWEEN '2026' AND '2026'", whereText);
        Assert.Contains("GLPCA~RACCT IN opt", whereText);
        Assert.Equal("0000600100", request.InputTablesItems["value_list"][0]["LOW"]);
    }

    [Fact]
    public void ParseProfitCenterRows_maps_pipe_delimited_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["DATA_display"] = new()
                {
                    new() { ["WA"] = "header|row|skipped|here|too|and|here" },
                    new() { ["WA"] = "600100|2000|2026|20260315|1.500,00|4500012345|10|30005R|CUST1|SO1|10" },
                }
            }
        };

        var rows = CostingHelper.ParseProfitCenterRows(response);

        Assert.Single(rows);
        Assert.Equal("600100", rows[0].GlAccount);
        Assert.Equal(1500.00m, rows[0].CompanyCodeValue);
        Assert.Equal("4500012345", rows[0].InvoiceNumber);
        Assert.Equal("SO1", rows[0].SalesOrder);
    }

    [Fact]
    public void BuildFreightPostingRequest_posts_the_vendor_as_a_debit_and_the_GL_line_as_a_credit()
    {
        var body = new FreightPostingRequest { DocDate = "20260315", Vendor = "12345", Amount = 100m, Currency = "GBP", GlAccount = "600100", ProfitCenter = "2000", Shipment = "SHIP1", Information = "Freight" };
        var request = CostingHelper.BuildFreightPostingRequest(body, null);

        var amounts = request.InputTables["CURRENCYAMOUNT"];
        Assert.Equal(-100m, amounts[0]["AMT_DOCCUR"]); // vendor line = debit (negative)
        Assert.Equal(100m, amounts[1]["AMT_DOCCUR"]);  // GL line = credit (positive)
    }

    [Fact]
    public void BuildFreightPostingRequest_defaults_payment_terms_to_ZB30_when_none_given()
    {
        var body = new FreightPostingRequest { DocDate = "20260315", Vendor = "12345", Amount = 100m, Currency = "GBP", GlAccount = "600100", ProfitCenter = "2000", Shipment = "SHIP1" };
        var withDefault = CostingHelper.BuildFreightPostingRequest(body, null);
        Assert.Equal("ZB30", withDefault.InputTables["ACCOUNTPAYABLE"][0]["PMNTTRMS"]);

        var withOverride = CostingHelper.BuildFreightPostingRequest(body, "ZB60");
        Assert.Equal("ZB60", withOverride.InputTables["ACCOUNTPAYABLE"][0]["PMNTTRMS"]);
    }

    [Fact]
    public void ParseFreightPostingRows_splits_the_document_number_out_of_OBJ_KEY()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["OBJ_KEY"] = "1900012345" + "2026" }, // BELNR (10) + GJAHR (4)
            Tables = new() { ["RETURN"] = [] },
        };
        var row = CostingHelper.ParseFreightPostingRows(response);
        Assert.Equal("1900012345", row.AccountingNumber);
        Assert.True(row.Success);
    }

    [Fact]
    public void ParseFreightPostingRows_leaves_AccountingNumber_blank_when_OBJ_KEY_is_too_short()
    {
        var response = new RfcResponse { Parameters = new() { ["OBJ_KEY"] = "short" }, Tables = new() { ["RETURN"] = [] } };
        var row = CostingHelper.ParseFreightPostingRows(response);
        Assert.Equal("", row.AccountingNumber);
    }
}
