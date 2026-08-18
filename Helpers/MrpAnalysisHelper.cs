using SapServer.Models;
using SapServer.Models.Bapi;
using System.Globalization;

namespace SapServer.Helpers;

/// <summary>
/// Supports MrpAnalysisController — year-on-year consumption/goods-receipt trends and
/// sales-forecast-to-raw-material BOM explosion for Normanton Nexus's MRP Analysis screen.
/// Consumption-by-year reuses PerformanceHelpers.BuildConsumptionHistoryRequest directly
/// (no new RFC shape needed there); this file holds the feature's own new RFC reads
/// (goods-receipt-by-vendor history, bulk BOM explosion support).
/// </summary>
internal static class MrpAnalysisHelper
{
    /// <summary>
    /// Single permission function code covering the whole MRP Analysis surface, rather than
    /// one per endpoint — several different endpoints here call different underlying RFCs
    /// (ZRFC_READ_TABLES against MVER/MSEG/MKPF/EKKO/ZBOM_INFO/MARC), same convention
    /// ProductionHelpers.FnCreate already uses for several endpoints that call different RFCs.
    /// </summary>
    internal const string FnMrpAnalysis = "MRP_ANALYSIS";

    internal const string FnReadTables = "ZRFC_READ_TABLES";
    internal const string Plant        = "3012";

    // ── Goods-receipt history (MSEG/MKPF), by material — vendor resolved separately ──────
    //
    // MSEG is the proven table for goods-receipt data in this codebase (see
    // ConsignmentHelpers.BuildVendorGrRequest — an actively-used, working MSEG+MKPF pull for
    // vendor consignment GR), not EKBE — reused here rather than guessing at a fresh table.
    // Same unfiltered-by-material bulk-pull-then-match-in-memory shape
    // PerformanceHelpers.BuildConsumptionHistoryRequest already established as safe for
    // plant-wide historical pulls (see that method's own comment about a batched-per-material
    // attempt disturbing other concurrent requests on the shared connection pool).
    //
    // Filtering is deliberately single-value EQ/GE only, no IN-opt/multi-value condition —
    // ConsignmentHelpers.BuildVendorGrRequest's own header comment documents a real, confirmed
    // failure in this RFC wrapper when an IN-opt condition (BWART) was combined with several
    // other WHERE conditions in one call: it silently returned zero rows. sinceDate (optional)
    // uses the same single-value MKPF~BUDAT GE comparison ConsignmentHelpers already relies on
    // successfully, rather than a GJAHR/year IN-opt list.
    //
    // MSEG-LIFNR is only populated by SAP for vendor-linked stock categories (consignment,
    // subcontracting) — blank for an ordinary PO goods receipt. Read it anyway (covers
    // consignment lines directly, no extra lookup needed for those) and fall back to the PO's
    // own EKKO-LIFNR via BuildPurchaseOrderVendorsRequest/AggregateGoodsReceiptHistory below
    // for everything else.
    private static readonly string[] MsegColumns = ["MATNR", "MENGE", "MEINS", "EBELN", "LIFNR"];
    private static readonly string[] MkpfColumns  = ["BUDAT"];

    internal static RfcRequest BuildGoodsReceiptHistoryRequest(string? sinceDate = null)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "MSEG" })
            .TableRow("QUERY_TABLES", new { TABNAME = "MKPF" });

        foreach (var f in MsegColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MSEG", FIELDNAME = f });
        foreach (var f in MkpfColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "MKPF", FIELDNAME = f });

        builder
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MSEG", FLD_FROM = "MANDT", TAB_TO = "MKPF", FLD_TO = "MANDT" })
            .TableItemRow("join_FIELDS", new { TAB_FROM = "MSEG", FLD_FROM = "MBLNR", TAB_TO = "MKPF", FLD_TO = "MBLNR" });

        builder
            .WhereCondition($"MSEG~WERKS EQ '{Plant}'")
            .WhereCondition("MSEG~BWART EQ '101'");

        if (!string.IsNullOrWhiteSpace(sinceDate))
            builder.WhereCondition($"MKPF~BUDAT GE '{sinceDate}'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static GoodsReceiptHistoryRawRow[] ParseGoodsReceiptHistoryRawRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        var expectedCols = MsegColumns.Length + MkpfColumns.Length;

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= expectedCols)
            .Select(cols => new GoodsReceiptHistoryRawRow
            {
                // Column order follows registration order: MsegColumns
                // (MATNR, MENGE, MEINS, EBELN, LIFNR) then MkpfColumns (BUDAT).
                //
                // Material is deliberately left in SAP's raw/padded form here (just trimmed),
                // NOT run through NormaliseMaterial — Normanton-Nexus's log.TurnsValClassSnapshot
                // (and everything joined to it — log.VendorMaterial, log.PurchaseOrderSuggestion)
                // stores Material the same raw/padded way ComputeTurnsRows outputs it
                // (Material = rawMaterial, never normalised), so this needs to match that
                // convention, not diverge from it, or a purely-numeric material's join to
                // TurnsValClassSnapshot would silently match zero rows even though the material
                // genuinely exists (mixed alnum codes like "30005R" happen to survive either way,
                // which is what let this slip through initially).
                Material      = cols[0].Trim(),
                Qty           = decimal.TryParse(cols[1].Trim(), out var qty) ? qty : 0m,
                Uom           = cols[2].Trim(),
                PurchaseOrder = cols[3].Trim(),
                Vendor        = cols[4].Trim(),
                Year          = ParseSapYear(cols[5]),
            })
            .Where(r => r.Year > 0)
            .ToArray();
    }

    // MKPF-BUDAT comes back from ZRFC_READ_TABLES's generic table read as "dd.MM.yyyy" —
    // confirmed by ConsignmentHelpers.ParseVendorGrRows' own real-data test fixture
    // (BLDAT/BUDAT rendered as e.g. "02.01.2026"), the same field off the same table this
    // codebase already reads elsewhere. yyyyMMdd is kept as a defensive fallback only, in
    // case a different date field/table ever serialises internally instead.
    private static int ParseSapYear(string raw)
    {
        var trimmed = raw.Trim();

        if (DateTime.TryParseExact(trimmed, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return d1.Year;

        if (trimmed.Length == 8 && trimmed.All(char.IsDigit) &&
            DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.Year;

        return 0;
    }

    // ── Purchase order vendor (EKKO) — fallback vendor resolution for a non-consignment PO ──
    //
    // Same unfiltered-by-EBELN bulk-pull shape as the goods-receipt read above — a
    // BSTYP-only filter (standard purchase orders) plus the optional single-value BEDAT GE
    // floor, no IN-opt.

    private static readonly string[] EkkoColumns = ["EBELN", "LIFNR"];

    internal static RfcRequest BuildPurchaseOrderVendorsRequest(string? sinceDate = null)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "EKKO" });

        foreach (var f in EkkoColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "EKKO", FIELDNAME = f });

        builder.WhereCondition("EKKO~BSTYP EQ 'F'"); // standard purchase order

        if (!string.IsNullOrWhiteSpace(sinceDate))
            builder.WhereCondition($"EKKO~BEDAT GE '{sinceDate}'");

        builder.ReadTable("data_display");
        return builder.Build();
    }

    internal static PurchaseOrderVendorRow[] ParsePurchaseOrderVendorRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= EkkoColumns.Length)
            .Select(cols => new PurchaseOrderVendorRow
            {
                PurchaseOrder = cols[0].Trim(),
                Vendor        = cols[1].Trim(),
            })
            .ToArray();
    }

    // ── Join + aggregate ─────────────────────────────────────────────────────────────────
    //
    // Pure in-memory join: resolves each raw line's vendor (its own MSEG-LIFNR if SAP
    // populated one — consignment/subcontracting lines — otherwise its PO's EKKO-LIFNR), then
    // sums quantity per (Material, Vendor, Year). A line whose vendor can't be resolved either
    // way (no LIFNR, no matching EKKO row) is skipped rather than grouped under a blank vendor
    // — nothing left silently uncategorised is grouped over "".

    internal static GoodsReceiptHistoryRow[] AggregateGoodsReceiptHistory(
        IEnumerable<GoodsReceiptHistoryRawRow> rawRows,
        IEnumerable<PurchaseOrderVendorRow> poVendors)
    {
        var vendorByPo = poVendors
            .Where(v => !string.IsNullOrWhiteSpace(v.PurchaseOrder))
            .GroupBy(v => v.PurchaseOrder)
            .ToDictionary(g => g.Key, g => g.First().Vendor);

        var totals = new Dictionary<(string Material, string Vendor, int Year), (decimal Qty, string Uom)>();

        foreach (var row in rawRows)
        {
            var vendor = !string.IsNullOrWhiteSpace(row.Vendor)
                ? row.Vendor
                : vendorByPo.GetValueOrDefault(row.PurchaseOrder, "");

            if (string.IsNullOrWhiteSpace(vendor)) continue;

            var key = (row.Material, vendor, row.Year);
            var existing = totals.GetValueOrDefault(key);
            totals[key] = (existing.Qty + row.Qty, string.IsNullOrEmpty(existing.Uom) ? row.Uom : existing.Uom);
        }

        return [.. totals.Select(kv => new GoodsReceiptHistoryRow
        {
            Material = kv.Key.Material,
            Vendor   = kv.Key.Vendor,
            Year     = kv.Key.Year,
            Qty      = kv.Value.Qty,
            Uom      = kv.Value.Uom,
        })];
    }

    // ── BOM explosion (sales forecast -> raw materials) ─────────────────────────────────
    //
    // ZBOM_INFO only ever returns one level (a material's immediate components) — see
    // ProductionHelpers.BuildBomRequest/BuildBomRequestBulk — so getting all the way down to
    // raw materials means calling it repeatedly, one "wave" per BOM depth, feeding each
    // wave's still-semi-finished components into the next. Component-quantity multiplication
    // (parentQty * row.ComponentQty) mirrors ProductionController.PostScrap's existing,
    // already-trusted-for-real-SAP-postings treatment of BomRow.ComponentQty as a straight
    // per-1-unit-of-parent multiplier.

    /// <summary>Raw-material profit centre (MARC-PRCTR) — comes back zero-padded from SAP, confirmed by ProductionHelpersTests.</summary>
    internal const string RawMaterialProfitCentre = "0000002012";

    /// <summary>
    /// Explodes a set of finished-good/quantity pairs down to raw materials, level by level,
    /// stopping each branch at profit centre <see cref="RawMaterialProfitCentre"/>. Takes the
    /// actual SAP calls as delegates (bulk BOM explosion for one wave's distinct materials,
    /// bulk profit-centre classification for that wave's newly-discovered components) so this
    /// orchestration — the part with real logic worth getting right — is testable with canned
    /// per-wave responses, with no pool/controller/HTTP involved.
    ///
    /// A material that reappears as its own descendant (a BOM cycle) or that's still
    /// unresolved when <paramref name="maxDepth"/> is hit is folded into the raw-material
    /// totals as-is (its accumulated quantity is real and shouldn't be silently dropped) but
    /// listed in <see cref="BomExplosionResult.Unresolved"/> rather than trusted the same as a
    /// genuine profit-centre-2012 raw material.
    /// </summary>
    internal static async Task<BomExplosionResult> ExplodeBom(
        IReadOnlyList<BomExplosionItem> items,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<BomRow[]>> explodeWave,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> classifyWave,
        CancellationToken ct,
        int maxDepth = 25)
    {
        var rawMaterials = new Dictionary<string, (decimal Qty, string Uom)>();
        var unresolved    = new HashSet<string>();

        var currentWave = new Dictionary<string, decimal>();
        foreach (var item in items)
        {
            var key = PerformanceHelpers.NormaliseMaterial(item.Material);
            currentWave[key] = currentWave.GetValueOrDefault(key) + item.Quantity;
        }

        var visited = new HashSet<string>(currentWave.Keys);

        for (var depth = 0; depth < maxDepth && currentWave.Count > 0; depth++)
        {
            var bomRows = await explodeWave(currentWave.Keys, ct);

            var componentKeys = bomRows
                .Select(r => PerformanceHelpers.NormaliseMaterial(r.Component))
                .Distinct()
                .ToArray();

            var profitCentres = componentKeys.Length > 0
                ? await classifyWave(componentKeys, ct)
                : new Dictionary<string, string>();

            var nextWave = new Dictionary<string, decimal>();

            foreach (var row in bomRows)
            {
                var parentKey = PerformanceHelpers.NormaliseMaterial(row.Material);
                if (!currentWave.TryGetValue(parentKey, out var parentQty)) continue;

                var componentKey = PerformanceHelpers.NormaliseMaterial(row.Component);
                var requiredQty  = parentQty * row.ComponentQty;

                var profitCentre  = profitCentres.GetValueOrDefault(componentKey, string.Empty);
                var isRawMaterial = profitCentre == RawMaterialProfitCentre;
                var isCycle       = !isRawMaterial && visited.Contains(componentKey);

                if (isRawMaterial || isCycle)
                {
                    var existing = rawMaterials.GetValueOrDefault(componentKey);
                    rawMaterials[componentKey] = (existing.Qty + requiredQty, string.IsNullOrEmpty(existing.Uom) ? row.ComponentUnit : existing.Uom);
                    if (isCycle) unresolved.Add(componentKey);
                }
                else
                {
                    nextWave[componentKey] = nextWave.GetValueOrDefault(componentKey) + requiredQty;
                }
            }

            visited.UnionWith(nextWave.Keys);
            currentWave = nextWave;
        }

        // Anything still left in currentWave when maxDepth is hit never got a chance to
        // resolve — same "stop and flag, don't silently drop" treatment as a cycle. No BOM
        // row was ever read for these, so there's no ComponentUnit to carry over — leave Uom
        // blank rather than guessing.
        foreach (var (material, qty) in currentWave)
        {
            var existing = rawMaterials.GetValueOrDefault(material);
            rawMaterials[material] = (existing.Qty + qty, existing.Uom ?? string.Empty);
            unresolved.Add(material);
        }

        return new BomExplosionResult
        {
            RawMaterials = [.. rawMaterials.Select(kv => new RawMaterialRequirement
            {
                Material = kv.Key,
                Quantity = kv.Value.Qty,
                Uom      = kv.Value.Uom,
            })],
            Unresolved = [.. unresolved],
        };
    }
}
