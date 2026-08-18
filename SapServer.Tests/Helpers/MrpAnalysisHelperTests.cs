using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class MrpAnalysisHelperTests
{
    // ── BuildGoodsReceiptHistoryRequest ─────────────────────────────────────────────

    [Fact]
    public void BuildGoodsReceiptHistoryRequest_filters_to_this_plant_and_movement_101_only_as_single_value_conditions()
    {
        // Deliberately no IN-opt/multi-value condition here — see
        // ConsignmentHelpers.BuildVendorGrRequest's own header comment for the
        // real, confirmed failure this codebase hit combining an IN-opt
        // condition with other WHERE conditions in one ZRFC_READ_TABLES call.
        var request = MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest();
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MSEG~WERKS EQ '3012'", whereText);
        Assert.Contains("MSEG~BWART EQ '101'", whereText);
        Assert.DoesNotContain("IN opt", whereText);
    }

    [Fact]
    public void BuildGoodsReceiptHistoryRequest_adds_a_posting_date_floor_only_when_sinceDate_is_given()
    {
        var withSince = MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest("01.01.2021");
        var withSinceText = string.Join(" ", withSince.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("MKPF~BUDAT GE '01.01.2021'", withSinceText);

        var withoutSince = MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest();
        var withoutSinceText = string.Join(" ", withoutSince.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.DoesNotContain("BUDAT", withoutSinceText);
    }

    [Fact]
    public void BuildGoodsReceiptHistoryRequest_joins_MSEG_to_MKPF_on_document_number()
    {
        var request = MrpAnalysisHelper.BuildGoodsReceiptHistoryRequest();
        var joinRows = request.InputTablesItems["join_FIELDS"];

        Assert.Contains(joinRows, r => (string?)r["FLD_FROM"] == "MBLNR" && (string?)r["FLD_TO"] == "MBLNR");
    }

    // ── ParseGoodsReceiptHistoryRawRows ─────────────────────────────────────────────

    private static RfcResponse GrResponse(params string[] dataRows)
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["WA"] = "MATNR|MENGE|MEINS|EBELN|LIFNR|BUDAT" } };
        rows.AddRange(dataRows.Select(r => new Dictionary<string, object?> { ["WA"] = r }));
        return new RfcResponse { Tables = new() { ["data_display"] = rows } };
    }

    [Fact]
    public void ParseGoodsReceiptHistoryRawRows_parses_every_field_including_the_calendar_year_from_BUDAT()
    {
        var response = GrResponse("30005R|100|KG|4500012345|0000012345|02.01.2026");

        var row = Assert.Single(MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(response));

        Assert.Equal("30005R", row.Material);
        Assert.Equal(100m, row.Qty);
        Assert.Equal("KG", row.Uom);
        Assert.Equal("4500012345", row.PurchaseOrder);
        Assert.Equal("0000012345", row.Vendor);
        Assert.Equal(2026, row.Year);
    }

    [Fact]
    public void ParseGoodsReceiptHistoryRawRows_keeps_a_purely_numeric_material_zero_padded_not_normalised()
    {
        // log.MaterialReceiptHistory (Normanton-Nexus) is joined against
        // log.TurnsValClassSnapshot.Material, which stores MATNR exactly as ComputeTurnsRows
        // outputs it — raw/padded, never NormaliseMaterial'd. Stripping leading zeros here
        // would silently break that join for any purely-numeric material.
        var response = GrResponse("000000000000100234|100|KG|4500012345|0000012345|02.01.2026");

        var row = Assert.Single(MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(response));

        Assert.Equal("000000000000100234", row.Material);
    }

    [Fact]
    public void ParseGoodsReceiptHistoryRawRows_leaves_Vendor_blank_when_MSEG_LIFNR_is_blank_on_an_ordinary_PO_receipt()
    {
        var response = GrResponse("30005R|100|KG|4500012345| |02.01.2026");

        var row = Assert.Single(MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(response));

        Assert.Equal("", row.Vendor);
    }

    [Fact]
    public void ParseGoodsReceiptHistoryRawRows_skips_a_row_whose_posting_date_cannot_be_parsed()
    {
        var response = GrResponse("30005R|100|KG|4500012345|0000012345|not-a-date");

        Assert.Empty(MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(response));
    }

    [Fact]
    public void ParseGoodsReceiptHistoryRawRows_returns_empty_when_there_is_no_data_display_table()
    {
        Assert.Empty(MrpAnalysisHelper.ParseGoodsReceiptHistoryRawRows(new RfcResponse()));
    }

    // ── BuildPurchaseOrderVendorsRequest / ParsePurchaseOrderVendorRows ─────────────

    [Fact]
    public void BuildPurchaseOrderVendorsRequest_filters_to_standard_purchase_orders_only()
    {
        var request = MrpAnalysisHelper.BuildPurchaseOrderVendorsRequest();
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("EKKO~BSTYP EQ 'F'", whereText);
    }

    [Fact]
    public void ParsePurchaseOrderVendorRows_parses_EBELN_and_LIFNR()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "EBELN|LIFNR" },
                    new() { ["WA"] = "4500012345|0000012345" },
                }
            }
        };

        var row = Assert.Single(MrpAnalysisHelper.ParsePurchaseOrderVendorRows(response));
        Assert.Equal("4500012345", row.PurchaseOrder);
        Assert.Equal("0000012345", row.Vendor);
    }

    // ── AggregateGoodsReceiptHistory ─────────────────────────────────────────────

    [Fact]
    public void AggregateGoodsReceiptHistory_uses_MSEG_LIFNR_directly_when_present()
    {
        var raw = new[] { new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 100m, Uom = "KG", PurchaseOrder = "PO1", Vendor = "0000012345", Year = 2026 } };

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(raw, []);

        var row = Assert.Single(rows);
        Assert.Equal("0000012345", row.Vendor);
        Assert.Equal(100m, row.Qty);
    }

    [Fact]
    public void AggregateGoodsReceiptHistory_falls_back_to_the_purchase_order_vendor_when_MSEG_LIFNR_is_blank()
    {
        var raw = new[] { new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 100m, Uom = "KG", PurchaseOrder = "PO1", Vendor = "", Year = 2026 } };
        var poVendors = new[] { new PurchaseOrderVendorRow { PurchaseOrder = "PO1", Vendor = "0000099999" } };

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(raw, poVendors);

        var row = Assert.Single(rows);
        Assert.Equal("0000099999", row.Vendor);
    }

    [Fact]
    public void AggregateGoodsReceiptHistory_drops_a_line_whose_vendor_cannot_be_resolved_either_way()
    {
        var raw = new[] { new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 100m, Uom = "KG", PurchaseOrder = "PO-UNKNOWN", Vendor = "", Year = 2026 } };

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(raw, []);

        Assert.Empty(rows);
    }

    [Fact]
    public void AggregateGoodsReceiptHistory_sums_multiple_lines_for_the_same_material_vendor_and_year()
    {
        var raw = new[]
        {
            new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 100m, Uom = "KG", PurchaseOrder = "PO1", Vendor = "0000012345", Year = 2026 },
            new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 50m,  Uom = "KG", PurchaseOrder = "PO2", Vendor = "0000012345", Year = 2026 },
        };

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(raw, []);

        var row = Assert.Single(rows);
        Assert.Equal(150m, row.Qty);
    }

    [Fact]
    public void AggregateGoodsReceiptHistory_keeps_the_same_material_separate_across_different_vendors_and_years()
    {
        var raw = new[]
        {
            new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 100m, Uom = "KG", PurchaseOrder = "PO1", Vendor = "0000012345", Year = 2025 },
            new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 200m, Uom = "KG", PurchaseOrder = "PO2", Vendor = "0000099999", Year = 2025 },
            new GoodsReceiptHistoryRawRow { Material = "30005R", Qty = 300m, Uom = "KG", PurchaseOrder = "PO1", Vendor = "0000012345", Year = 2026 },
        };

        var rows = MrpAnalysisHelper.AggregateGoodsReceiptHistory(raw, []);

        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, r => r.Vendor == "0000012345" && r.Year == 2025 && r.Qty == 100m);
        Assert.Contains(rows, r => r.Vendor == "0000099999" && r.Year == 2025 && r.Qty == 200m);
        Assert.Contains(rows, r => r.Vendor == "0000012345" && r.Year == 2026 && r.Qty == 300m);
    }
}

// ExplodeBom's real work — the recursive wave loop, quantity multiplication, cycle guard,
// and depth cutoff — is pure orchestration over injected delegates, so it's tested here with
// no pool/controller/HTTP involved at all: canned per-wave BOM/profit-centre responses,
// driven purely by which materials each wave asks for.
public class MrpAnalysisHelperExplodeBomTests
{
    private static Func<IReadOnlyCollection<string>, CancellationToken, Task<BomRow[]>> ExplodeWave(Dictionary<string, BomRow[]> bomByParent) =>
        (materials, _) => Task.FromResult(materials.SelectMany(m => bomByParent.GetValueOrDefault(m, [])).ToArray());

    private static Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> Classify(Dictionary<string, string> profitCentreByMaterial) =>
        (materials, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(
            materials.Where(profitCentreByMaterial.ContainsKey).ToDictionary(m => m, m => profitCentreByMaterial[m]));

    [Fact]
    public async Task ExplodeBom_stops_at_a_single_level_raw_material_and_multiplies_by_the_forecast_quantity()
    {
        var bom = new Dictionary<string, BomRow[]>
        {
            ["FG1"] = [new BomRow { Material = "FG1", Component = "RAW1", ComponentQty = 2m, ComponentUnit = "KG" }],
        };
        var pc = new Dictionary<string, string> { ["RAW1"] = MrpAnalysisHelper.RawMaterialProfitCentre };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "FG1", Quantity = 10m }],
            ExplodeWave(bom), Classify(pc), CancellationToken.None);

        var row = Assert.Single(result.RawMaterials);
        Assert.Equal("RAW1", row.Material);
        Assert.Equal(20m, row.Quantity); // 10 * 2
        Assert.Equal("KG", row.Uom);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public async Task ExplodeBom_recurses_through_a_semi_finished_level_multiplying_at_each_step()
    {
        var bom = new Dictionary<string, BomRow[]>
        {
            ["FG1"]   = [new BomRow { Material = "FG1", Component = "SEMI1", ComponentQty = 3m, ComponentUnit = "EA" }],
            ["SEMI1"] = [new BomRow { Material = "SEMI1", Component = "RAW1", ComponentQty = 2m, ComponentUnit = "KG" }],
        };
        var pc = new Dictionary<string, string>
        {
            ["SEMI1"] = "0000001000", // not the raw-material profit centre — keep exploding
            ["RAW1"]  = MrpAnalysisHelper.RawMaterialProfitCentre,
        };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "FG1", Quantity = 10m }],
            ExplodeWave(bom), Classify(pc), CancellationToken.None);

        var row = Assert.Single(result.RawMaterials);
        Assert.Equal("RAW1", row.Material);
        Assert.Equal(60m, row.Quantity); // 10 * 3 * 2
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public async Task ExplodeBom_sums_the_same_raw_material_needed_by_two_different_finished_goods()
    {
        var bom = new Dictionary<string, BomRow[]>
        {
            ["FG1"] = [new BomRow { Material = "FG1", Component = "RAW1", ComponentQty = 1m, ComponentUnit = "KG" }],
            ["FG2"] = [new BomRow { Material = "FG2", Component = "RAW1", ComponentQty = 1m, ComponentUnit = "KG" }],
        };
        var pc = new Dictionary<string, string> { ["RAW1"] = MrpAnalysisHelper.RawMaterialProfitCentre };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "FG1", Quantity = 10m }, new BomExplosionItem { Material = "FG2", Quantity = 5m }],
            ExplodeWave(bom), Classify(pc), CancellationToken.None);

        var row = Assert.Single(result.RawMaterials);
        Assert.Equal(15m, row.Quantity); // 10 + 5
    }

    [Fact]
    public async Task ExplodeBom_folds_a_BOM_cycle_into_raw_materials_flagged_as_unresolved_instead_of_looping_forever()
    {
        // A -> B -> A. Neither is ever classified at the raw-material profit centre.
        var bom = new Dictionary<string, BomRow[]>
        {
            ["A"] = [new BomRow { Material = "A", Component = "B", ComponentQty = 1m, ComponentUnit = "KG" }],
            ["B"] = [new BomRow { Material = "B", Component = "A", ComponentQty = 1m, ComponentUnit = "KG" }],
        };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "A", Quantity = 10m }],
            ExplodeWave(bom), Classify([]), CancellationToken.None, maxDepth: 25);

        // A (depth 0 seed) -> B (depth-1 component, new) -> A again (depth-2 component, but
        // A was already visited at depth 0) — the second A is the detected cycle.
        Assert.Contains("A", result.Unresolved);
        Assert.Contains(result.RawMaterials, r => r.Material == "A");
    }

    [Fact]
    public async Task ExplodeBom_stops_at_maxDepth_and_flags_whatever_is_still_unresolved_rather_than_hanging()
    {
        // A fresh material at every level (no cycle, never reaches the raw-material profit
        // centre) — without a depth cap this would recurse forever.
        var bom = new Dictionary<string, BomRow[]>
        {
            ["L0"] = [new BomRow { Material = "L0", Component = "L1", ComponentQty = 1m, ComponentUnit = "KG" }],
            ["L1"] = [new BomRow { Material = "L1", Component = "L2", ComponentQty = 1m, ComponentUnit = "KG" }],
            ["L2"] = [new BomRow { Material = "L2", Component = "L3", ComponentQty = 1m, ComponentUnit = "KG" }],
        };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "L0", Quantity = 10m }],
            ExplodeWave(bom), Classify([]), CancellationToken.None, maxDepth: 2);

        // Depth 0 explodes L0->L1, depth 1 explodes L1->L2 — L2 never gets its own wave
        // (maxDepth=2 means only 2 iterations run), so it's left over and flagged.
        Assert.Contains("L2", result.Unresolved);
        Assert.Contains(result.RawMaterials, r => r.Material == "L2" && r.Quantity == 10m);
    }

    [Fact]
    public async Task ExplodeBom_ignores_a_BOM_row_whose_parent_is_not_in_the_current_wave()
    {
        // Defensive: if the SAP response ever includes a row for a material this wave didn't
        // ask about, it must not be treated as if it were.
        var bom = new Dictionary<string, BomRow[]>
        {
            ["FG1"] = [
                new BomRow { Material = "FG1", Component = "RAW1", ComponentQty = 1m, ComponentUnit = "KG" },
                new BomRow { Material = "SOME-OTHER-MATERIAL", Component = "RAW2", ComponentQty = 1m, ComponentUnit = "KG" },
            ],
        };
        var pc = new Dictionary<string, string> { ["RAW1"] = MrpAnalysisHelper.RawMaterialProfitCentre, ["RAW2"] = MrpAnalysisHelper.RawMaterialProfitCentre };

        var result = await MrpAnalysisHelper.ExplodeBom(
            [new BomExplosionItem { Material = "FG1", Quantity = 10m }],
            ExplodeWave(bom), Classify(pc), CancellationToken.None);

        Assert.DoesNotContain(result.RawMaterials, r => r.Material == "RAW2");
    }

    [Fact]
    public async Task ExplodeBom_returns_no_raw_materials_for_an_empty_item_list()
    {
        var result = await MrpAnalysisHelper.ExplodeBom([], ExplodeWave([]), Classify([]), CancellationToken.None);
        Assert.Empty(result.RawMaterials);
        Assert.Empty(result.Unresolved);
    }
}
