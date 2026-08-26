using SapServer.Helpers;
using SapServer.Models;

namespace SapServer.Tests.Helpers;

public class SalesHelpersTests
{
    private static ScheduleWaterfallRequest BaseRequest() => new()
    {
        SalesOrg = "302",
        ShipToParties = ["302879"],
        Materials = ["31446702"],
        IncludeForecast = true,
        IncludeJit = false,
        IdocCreatedAfter = new DateTime(2020, 1, 1),
        ScheduleDateFrom = new DateTime(2020, 1, 1),
        ScheduleDateTo = new DateTime(2020, 12, 31),
    };

    [Fact]
    public void BuildHistoryRequest_queries_VBAK_VBAP_VBLB_VBEH_joined_on_document_keys()
    {
        var request = SalesHelpers.BuildHistoryRequest(BaseRequest());

        var tables = request.InputTables["QUERY_TABLES"].Select(r => r["TABNAME"]).ToArray();
        Assert.Equal(["VBAK", "VBAP", "VBLB", "VBEH"], tables);

        var joins = request.InputTablesItems["join_FIELDS"];
        Assert.Contains(joins, j => Equals(j["TAB_FROM"], "VBLB") && Equals(j["TAB_TO"], "VBEH") && Equals(j["FLD_TO"], "ABRLI"));
        Assert.Contains(joins, j => Equals(j["TAB_FROM"], "VBLB") && Equals(j["TAB_TO"], "VBEH") && Equals(j["FLD_TO"], "ABART"));
        Assert.Contains(joins, j => Equals(j["TAB_FROM"], "VBAK") && Equals(j["TAB_TO"], "VBAP") && Equals(j["FLD_TO"], "VBELN"));
    }

    [Fact]
    public void BuildHistoryRequest_pads_sales_org_ship_to_and_material_and_filters_release_type()
    {
        var request = SalesHelpers.BuildHistoryRequest(BaseRequest());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("VBAK~VKORG EQ '0302'", whereText); // n2c(sales_org, 4)
        Assert.Contains("VBAK~KUNNR IN opt", whereText);
        Assert.Contains("VBAP~MATNR IN opt", whereText);
        Assert.Contains("VBLB~ABART IN opt", whereText);
        Assert.Contains("VBLB~ERDAT GT '20200101'", whereText);

        var valueList = request.InputTablesItems["value_list"];
        Assert.Contains(valueList, v => Equals(v["FIELDNAME"], "KUNNR") && Equals(v["LOW"], "0000302879"));
        Assert.Contains(valueList, v => Equals(v["FIELDNAME"], "MATNR") && Equals(v["LOW"], "000000000031446702"));
        Assert.Contains(valueList, v => Equals(v["FIELDNAME"], "ABART") && Equals(v["LOW"], "1")); // Forecast only
        Assert.DoesNotContain(valueList, v => Equals(v["FIELDNAME"], "ABART") && Equals(v["LOW"], "2")); // JIT excluded
    }

    [Fact]
    public void BuildHistoryRequest_pushes_schedule_date_and_zero_qty_filters_to_SAP_by_default()
    {
        // "Can't have wc when outer join" per the source tool — these filters
        // only apply on the default (IncludeZeroQty = false) inner-join path.
        var request = SalesHelpers.BuildHistoryRequest(BaseRequest());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("VBEH~EDATU IN opt", whereText);
        Assert.Contains("VBEH~WMENG > 0", whereText);

        var withZero = SalesHelpers.BuildHistoryRequest(BaseRequest() with { IncludeZeroQty = true });
        var withZeroWhereText = string.Join(" ", withZero.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.DoesNotContain("VBEH~EDATU IN opt", withZeroWhereText);
        Assert.DoesNotContain("VBEH~WMENG > 0", withZeroWhereText);
    }

    [Fact]
    public void BuildHistoryRequest_omits_material_filter_entirely_when_no_materials_given()
    {
        var request = SalesHelpers.BuildHistoryRequest(BaseRequest() with { Materials = [] });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.DoesNotContain("MATNR", whereText);
    }

    [Fact]
    public void BuildCurrentRequest_queries_VBEP_and_restricts_to_the_current_release_line()
    {
        var request = SalesHelpers.BuildCurrentRequest(BaseRequest());

        var tables = request.InputTables["QUERY_TABLES"].Select(r => r["TABNAME"]).ToArray();
        Assert.Equal(["VBAK", "VBAP", "VBLB", "VBEP"], tables);

        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("VBLB~ABRLI EQ '0'", whereText);
        Assert.Contains("VBEP~EDATU IN opt", whereText);
        Assert.Contains("VBEP~WMENG > 0", whereText);

        var joins = request.InputTablesItems["join_FIELDS"];
        Assert.Contains(joins, j => Equals(j["TAB_FROM"], "VBLB") && Equals(j["TAB_TO"], "VBEP") && Equals(j["FLD_TO"], "ABART"));
        Assert.DoesNotContain(joins, j => Equals(j["TAB_TO"], "VBEP") && Equals(j["FLD_TO"], "ABRLI"));
    }

    private static RfcResponse SingleRowResponse(string wa) => new()
    {
        Tables = new()
        {
            ["data_display"] = new()
            {
                new() { ["WA"] = "KUNNR|VBELN|POSNR|MATNR|ARKTX|DOCNUM|ABEFZ|ERDAT|ERZEI|EDATU|WMENG|VKORG|ABART|LFNKD" },
                new() { ["WA"] = wa },
            }
        }
    };

    [Fact]
    public void ParseRows_maps_columns_in_query_FIELDS_order_and_derives_ISO_week_numbers()
    {
        var response = SingleRowResponse(
            "0000302879|30103645|000010|31446702|SV FSC ADU OWS SPA VCC|25753188|150|20200102|080000|20200106|540|0302|1|3");

        var rows = SalesHelpers.ParseRows(response, isCurrent: false);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("0000302879", row.ShipToParty);
        Assert.Equal("31446702", row.Material);
        Assert.Equal("25753188", row.IdocNumber);
        Assert.Equal(150m, row.CumQty);
        Assert.Equal(new DateTime(2020, 1, 2), row.IdocCreationDate);
        Assert.Equal(new DateTime(2020, 1, 6), row.ScheduleLineDate);
        Assert.Equal(540m, row.OrderQty);
        Assert.Equal("1", row.ReleaseType);
        Assert.False(row.IsCurrent);
        // 2020-01-02 (Thu) is ISO week 1 of 2020; 2020-01-06 (Mon) is ISO week 2.
        Assert.Equal(202001, row.IdocWeek);
        Assert.Equal(202002, row.ScheduleWeek);
    }

    [Fact]
    public void ParseRows_treats_00000000_as_a_null_date()
    {
        var response = SingleRowResponse(
            "0000302879|30103645|000010|31446702|SV FSC ADU OWS SPA VCC|25753188|0|00000000|000000|20200106|540|0302|1|3");

        var row = SalesHelpers.ParseRows(response, isCurrent: true).Single();

        Assert.Null(row.IdocCreationDate);
        Assert.Equal(0, row.IdocWeek);
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public void ComputeCumulativeRelease_runs_a_separate_total_per_ship_to_material_and_release()
    {
        var rows = new[]
        {
            new ScheduleWaterfallRow { ShipToParty = "A", Material = "M1", IdocNumber = "IDOC1", ScheduleLineDate = new DateTime(2020, 1, 6), OrderQty = 100m },
            new ScheduleWaterfallRow { ShipToParty = "A", Material = "M1", IdocNumber = "IDOC1", ScheduleLineDate = new DateTime(2020, 1, 13), OrderQty = 50m },
            // Different idoc (different release/pull) — separate running total, not summed with IDOC1's.
            new ScheduleWaterfallRow { ShipToParty = "A", Material = "M1", IdocNumber = "IDOC2", ScheduleLineDate = new DateTime(2020, 1, 6), OrderQty = 200m },
            // Current (live) schedule rows share the "current" bucket regardless of IdocNumber.
            new ScheduleWaterfallRow { ShipToParty = "A", Material = "M1", IsCurrent = true, ScheduleLineDate = new DateTime(2020, 1, 6), OrderQty = 10m },
            new ScheduleWaterfallRow { ShipToParty = "A", Material = "M1", IsCurrent = true, ScheduleLineDate = new DateTime(2020, 1, 20), OrderQty = 5m },
        };

        var result = SalesHelpers.ComputeCumulativeRelease(rows).ToArray();

        var idoc1 = result.Where(r => r.IdocNumber == "IDOC1").OrderBy(r => r.ScheduleLineDate).ToArray();
        Assert.Equal(100m, idoc1[0].CumulativeRelease);
        Assert.Equal(150m, idoc1[1].CumulativeRelease);

        var idoc2 = result.Single(r => r.IdocNumber == "IDOC2");
        Assert.Equal(200m, idoc2.CumulativeRelease);

        var current = result.Where(r => r.IsCurrent).OrderBy(r => r.ScheduleLineDate).ToArray();
        Assert.Equal(10m, current[0].CumulativeRelease);
        Assert.Equal(15m, current[1].CumulativeRelease);
    }
}
