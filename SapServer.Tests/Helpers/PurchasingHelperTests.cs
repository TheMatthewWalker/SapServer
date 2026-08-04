using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class PurchasingHelperTests
{
    private static PoCreateRequest MinimalRequest(params PoCreateItem[] items) => new()
    {
        Vendor = "12345",
        Currency = "GBP",
        DocDate = "01.02.2026",
        Items = [.. items],
    };

    [Fact]
    public void BuildPoCreateRequest_pads_vendor_and_sets_the_fixed_header_constants()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(new PoCreateItem { ShortText = "Line 1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }));

        var header = request.StructImportParameters["POHEADER"];
        Assert.Equal("0000012345", header["VENDOR"]);
        Assert.Equal("0312", header["COMP_CODE"]);
        Assert.Equal("3012", header["PURCH_ORG"]);
        Assert.Equal("386", header["PUR_GROUP"]);
        Assert.Equal("NB", header["DOC_TYPE"]);
        Assert.Equal("GBP", header["CURRENCY"]);
    }

    [Fact]
    public void BuildPoCreateRequest_flags_every_header_field_as_changed_in_POHEADERX()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(new PoCreateItem { ShortText = "Line 1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }));
        var headerX = request.StructImportParameters["POHEADERX"];
        Assert.All(headerX.Values, v => Assert.Equal("X", v));
    }

    [Theory]
    [InlineData("01.02.2026", "20260201")]
    [InlineData("20260201", "20260201")]
    public void BuildPoCreateRequest_normalises_DocDate_to_yyyyMMdd(string input, string expected)
    {
        var body = new PoCreateRequest
        {
            Vendor = "12345",
            Currency = "GBP",
            DocDate = input,
            Items = [new PoCreateItem { ShortText = "L", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }],
        };
        var request = PurchasingHelper.BuildPoCreateRequest(body);
        Assert.Equal(expected, request.StructImportParameters["POHEADER"]["DOC_DATE"]);
    }

    [Fact]
    public void BuildPoCreateRequest_numbers_items_00010_00020_00030()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" },
            new PoCreateItem { ShortText = "L2", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" },
            new PoCreateItem { ShortText = "L3", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }
        ));

        var items = request.InputTables["POITEM"];
        Assert.Equal(["00010", "00020", "00030"], items.Select(i => i["PO_ITEM"]));
    }

    [Fact]
    public void BuildPoCreateRequest_omits_NET_PRICE_entirely_when_none_supplied_so_SAP_can_determine_its_own_price()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 5, Unit = "EA", DeliveryDate = "01.02.2026", NetPrice = null }
        ));

        var item = request.InputTables["POITEM"][0];
        var itemX = request.InputTables["POITEMX"][0];
        Assert.False(item.ContainsKey("NET_PRICE"));
        Assert.False(item.ContainsKey("PRICE_UNIT"));
        Assert.False(itemX.ContainsKey("NET_PRICE"));
    }

    [Fact]
    public void BuildPoCreateRequest_sets_PRICE_UNIT_to_quantity_when_a_net_price_is_supplied()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 5.5m, Unit = "EA", DeliveryDate = "01.02.2026", NetPrice = 12.345m }
        ));

        var item = request.InputTables["POITEM"][0];
        Assert.Equal(12.34m, item["NET_PRICE"]); // Math.Round uses banker's rounding by default: 12.345 -> 12.34 (rounds to even)
        Assert.Equal(5.5m, item["PRICE_UNIT"]);  // matches quantity, not NetPrice
    }

    [Fact]
    public void BuildPoCreateRequest_only_sends_MATERIAL_when_a_material_is_given()
    {
        var withMaterial = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026", Material = "30005r" }
        ));
        // SapPad.Pad only zero-pads/upper-cases a purely-numeric string — "30005r"
        // is mixed alnum, so it passes through unchanged (see SapPadTests for the
        // full writeup of this already-flagged quirk).
        Assert.Equal("30005r", withMaterial.InputTables["POITEM"][0]["MATERIAL"]);

        var withoutMaterial = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }
        ));
        Assert.False(withoutMaterial.InputTables["POITEM"][0].ContainsKey("MATERIAL"));
    }

    [Fact]
    public void BuildPoCreateRequest_omits_account_assignment_rows_when_AcctAssCat_is_not_set()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026" }
        ));
        Assert.False(request.InputTables.ContainsKey("POACCOUNT"));
    }

    [Fact]
    public void BuildPoCreateRequest_uses_COSTCENTER_for_AcctAssCat_K()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026", AcctAssCat = "K", CostCenterOrOrder = "4100", GlAccount = "600100" }
        ));
        var acct = request.InputTables["POACCOUNT"][0];
        Assert.Equal("0000004100", acct["COSTCENTER"]);
        Assert.False(acct.ContainsKey("ORDERID"));
    }

    [Fact]
    public void BuildPoCreateRequest_uses_ORDERID_for_AcctAssCat_F()
    {
        var request = PurchasingHelper.BuildPoCreateRequest(MinimalRequest(
            new PoCreateItem { ShortText = "L1", Quantity = 1, Unit = "EA", DeliveryDate = "01.02.2026", AcctAssCat = "F", CostCenterOrOrder = "500001", GlAccount = "600100" }
        ));
        var acct = request.InputTables["POACCOUNT"][0];
        Assert.Equal("0000500001", acct["ORDERID"]);
        Assert.False(acct.ContainsKey("COSTCENTER"));
    }

    [Fact]
    public void ParsePoCreateResult_reports_success_when_a_PO_number_comes_back()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["EXPPURCHASEORDER"] = "4500012345" },
            Tables = new() { ["RETURN"] = [] },
        };
        var row = PurchasingHelper.ParsePoCreateResult(response);
        Assert.True(row.Success);
        Assert.Equal("4500012345", row.PurchaseOrder);
    }

    [Fact]
    public void ParsePoCreateResult_reports_failure_when_no_PO_number_comes_back()
    {
        var response = new RfcResponse
        {
            Parameters = new(),
            Tables = new() { ["RETURN"] = [new() { ["TYPE"] = "E", ["MESSAGE"] = "No authorization" }] },
        };
        var row = PurchasingHelper.ParsePoCreateResult(response);
        Assert.False(row.Success);
        Assert.Equal("", row.PurchaseOrder);
        Assert.Single(row.Messages);
        Assert.Equal("No authorization", row.Messages[0].Message);
    }
}
