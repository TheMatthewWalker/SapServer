using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class GoodsMovementHelperTests
{
    private static GoodsMovementRequest MinimalBody(params GoodsMovementComponent[] components)
    {
        var list = components.Length > 0
            ? components.ToList()
            : [new GoodsMovementComponent { Material = "30007R", Quantity = 12.5m, Unit = "KG", StorageLocation = "1710" }];
        return new GoodsMovementRequest { Material = "TCEL9-9CBT", Header = "CO00000123", Components = list };
    }

    [Fact]
    public void BuildGoodsMovementRequest_uses_GM_CODE_06_the_only_code_confirmed_working_on_this_system()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(MinimalBody(), new Dictionary<string, string>());
        Assert.Equal(StockAdjustmentHelper.GmCodeOtherGoodsMovement, request.StructImportParameters["GOODSMVT_CODE"]["GM_CODE"]);
        Assert.Equal("06", request.StructImportParameters["GOODSMVT_CODE"]["GM_CODE"]);
    }

    [Fact]
    public void BuildGoodsMovementRequest_writes_one_GOODSMVT_ITEM_row_per_component()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(MinimalBody(
            new GoodsMovementComponent { Material = "30007R", Quantity = 12.5m, Unit = "KG", StorageLocation = "1710" },
            new GoodsMovementComponent { Material = "40012X", Quantity = 3.2m, Unit = "KG" }
        ), new Dictionary<string, string>());

        var rows = request.InputTables["GOODSMVT_ITEM"];
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void BuildGoodsMovementRequest_pads_and_upper_cases_the_component_material()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(MinimalBody(
            new GoodsMovementComponent { Material = "30007r", Quantity = 1m, Unit = "KG" }
        ), new Dictionary<string, string>());

        var row = request.InputTables["GOODSMVT_ITEM"][0];
        Assert.Equal("30007R", row["MATERIAL"]);
    }

    [Fact]
    public void BuildGoodsMovementRequest_uses_the_requests_own_movement_type_for_every_line()
    {
        var body = MinimalBody(
            new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG" },
            new GoodsMovementComponent { Material = "40012X", Quantity = 1m, Unit = "KG" }
        );
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(body, new Dictionary<string, string>());

        var rows = request.InputTables["GOODSMVT_ITEM"];
        Assert.All(rows, r => Assert.Equal(body.MovementType, r["MOVE_TYPE"]));
    }

    [Fact]
    public void BuildGoodsMovementRequest_prefers_the_components_own_storage_location_over_the_resolved_fallback()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(
            MinimalBody(new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG", StorageLocation = "1710" }),
            new Dictionary<string, string> { ["30007R"] = "1711" });

        var row = request.InputTables["GOODSMVT_ITEM"][0];
        Assert.Equal("1710", row["STGE_LOC"]);
    }

    [Fact]
    public void BuildGoodsMovementRequest_falls_back_to_the_resolved_storage_location_when_the_component_has_none()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(
            MinimalBody(new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG" }),
            new Dictionary<string, string> { ["30007R"] = "1711" });

        var row = request.InputTables["GOODSMVT_ITEM"][0];
        Assert.Equal("1711", row["STGE_LOC"]);
    }

    [Fact]
    public void BuildGoodsMovementRequest_omits_STGE_LOC_entirely_when_nothing_resolved_it()
    {
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(
            MinimalBody(new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG" }),
            new Dictionary<string, string>());

        var row = request.InputTables["GOODSMVT_ITEM"][0];
        Assert.False(row.ContainsKey("STGE_LOC"));
    }

    [Fact]
    public void BuildGoodsMovementRequest_truncates_the_header_reference_to_16_characters()
    {
        var body = new GoodsMovementRequest
        {
            Material = "TCEL9-9CBT", Header = "CO000001230000000000EXTRA",
            Components = [new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG" }],
        };
        var request = GoodsMovementHelper.BuildGoodsMovementRequest(body, new Dictionary<string, string>());
        Assert.Equal("CO000001230000000000EXTRA"[..16], request.StructImportParameters["GOODSMVT_HEADER"]["REF_DOC_NO"]);
    }

    [Fact]
    public void BuildGoodsMovementRequest_sets_TESTRUN_X_only_when_requested()
    {
        var live = GoodsMovementHelper.BuildGoodsMovementRequest(MinimalBody(), new Dictionary<string, string>());
        Assert.Equal("", live.ImportParameters["TESTRUN"]);

        var testBody = new GoodsMovementRequest
        {
            Material = "TCEL9-9CBT", Header = "CO00000123", TestRun = true,
            Components = [new GoodsMovementComponent { Material = "30007R", Quantity = 1m, Unit = "KG" }],
        };
        var test = GoodsMovementHelper.BuildGoodsMovementRequest(testBody, new Dictionary<string, string>());
        Assert.Equal("X", test.ImportParameters["TESTRUN"]);
    }

    [Fact]
    public void ParseGoodsMovementResponse_succeeds_only_when_a_material_document_came_back_with_no_blocking_error()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["MATERIALDOCUMENT"] = "5000012345", ["MATDOCUMENTYEAR"] = "2026" },
            Tables     = new() { ["RETURN"] = [] },
        };

        var result = GoodsMovementHelper.ParseGoodsMovementResponse(response);

        Assert.True(result.Success);
        Assert.Equal("5000012345", result.MaterialDocument);
        Assert.Equal("2026", result.MaterialDocumentYear);
    }

    [Fact]
    public void ParseGoodsMovementResponse_fails_when_no_material_document_came_back()
    {
        var response = new RfcResponse
        {
            Parameters = new(),
            Tables     = new() { ["RETURN"] = [new() { ["TYPE"] = "E", ["MESSAGE"] = "Storage location does not exist" }] },
        };

        var result = GoodsMovementHelper.ParseGoodsMovementResponse(response);

        Assert.False(result.Success);
        Assert.Contains(result.Messages, m => m.Type == "E");
    }

    [Fact]
    public void ParseGoodsMovementResponse_fails_when_RETURN_has_a_blocking_error_even_if_a_document_number_is_present()
    {
        // BAPI_GOODSMVT_CREATE surfaces business errors as TYPE E/A rows in
        // RETURN while Call() itself still returns true — a stray/leftover
        // MATERIALDOCUMENT value must not be trusted on its own.
        var response = new RfcResponse
        {
            Parameters = new() { ["MATERIALDOCUMENT"] = "5000012345" },
            Tables     = new() { ["RETURN"] = [new() { ["TYPE"] = "A", ["MESSAGE"] = "Posting period not open" }] },
        };

        var result = GoodsMovementHelper.ParseGoodsMovementResponse(response);
        Assert.False(result.Success);
    }
}
