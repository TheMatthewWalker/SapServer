using System.Data;
using System.Net;
using System.Web.Http;
using Microsoft.Identity.Client.NativeInterop;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[RoutePrefix("api/production")]
public sealed class ProductionController : SapControllerBase
{
    public ProductionController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<ProductionController> logger)
        : base(pool, permissions, logger) { }

    // ── POST /api/production/backflush ──────────────────────────────────

    [HttpPost]

    [Route("backflush")]
    public async Task<IHttpActionResult> Backflush(

        [FromBody] Zf40nRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "backflush");

        var charge = await _pool.ExecuteAsync(ProductionHelpers.BuildRequiresCharge(body.Material), ct);
        var zf40n    = await _pool.ExecuteAsync(
            ProductionHelpers.BuildZf40nRequest(
                body,
                ProductionHelpers.ParseRequiresCharge(charge)),
            ct);
        var response = ProductionHelpers.ParseBdcResponse(zf40n);
        _logger.LogInformation("Backflushing: " + body.Material + " x " + body.Quantity + " || " + response.RawMessage);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }

// ── POST /api/production/drumming-backflush ──────────────────────────
//
// Drumming's one point of difference from every other production process:
// the finished drum also needs a row in two custom SAP tables (ZPRODBATCH_TBL,
// ZBATCHPACK_TBL) via Z_ZPRODBATCH_MAINT — see ProductionHelpers'
// BuildProdBatchMaintRequest for the full rationale. This endpoint runs the
// same ZF40N backflush as plain /backflush, then chains on: finding the
// batch SAP just assigned the finished good (MSEG, movement 131), comparing
// that batch's material against the operator's traceability entries against
// this material's BOM (mistake-prevention only — never blocks; a mismatch
// here means the physical drum already exists, the data just needs fixing
// and Node/PROD_SUPERVISOR need to know), and finally writing the batch/pack
// rows. Everything after the backflush itself only runs if the backflush
// produced a material document — if it didn't, there's no batch or material
// document to hang any of the rest off, so the endpoint returns early with
// just the backflush result (same "always 200, Node reads Type" convention
// as plain /backflush).

    [HttpPost]

    [Route("drumming-backflush")]
    public async Task<IHttpActionResult> DrummingBackflush(

        [FromBody] DrumBackflushRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var zf40nBody = new Zf40nRequest
        {
            Material = body.Material,
            Quantity = body.Quantity,
            Header   = body.Header,
            Packaging = body.PackCode,
            Customer  = body.Customer,
        };

        var chargeCheck = await _pool.ExecuteAsync(ProductionHelpers.BuildRequiresCharge(body.Material), ct);
        var zf40n       = await _pool.ExecuteAsync(
            ProductionHelpers.BuildZf40nRequest(zf40nBody, ProductionHelpers.ParseRequiresCharge(chargeCheck)), ct);
        var backflush   = ProductionHelpers.ParseBdcResponse(zf40n);
        _logger.LogInformation("Drumming backflush: " + body.Material + " x " + body.Quantity + " || " + backflush.RawMessage);

        if (string.IsNullOrWhiteSpace(backflush.DocumentNumber))
            return Ok(ApiResponse<DrumBackflushResponse>.Ok(new DrumBackflushResponse { Backflush = backflush }));

        var msegData      = await _pool.ExecuteAsync(
            ProductionHelpers.BuildFindProducedBatchRequest(backflush.DocumentNumber, body.Material), ct);
        var producedBatch = ProductionHelpers.ParseProducedBatchRows(msegData).FirstOrDefault();

        if (producedBatch == null || string.IsNullOrWhiteSpace(producedBatch.Charge))
        {
            _logger.LogInformation($"Drumming backflush: no batch found for MatDoc {backflush.DocumentNumber} / {body.Material} — skipping Z_ZPRODBATCH_MAINT.");
            return Ok(ApiResponse<DrumBackflushResponse>.Ok(new DrumBackflushResponse
            {
                Backflush = backflush,
                MaterialDocument = backflush.DocumentNumber,
            }));
        }

        // BOM vs traceability — informational only, never blocks the batch/pack
        // write below. NormaliseMaterial strips leading zeros on numeric
        // material strings so SAP-padded BOM components and Node's bare
        // stored materials compare equal regardless of which side is padded.
        var bomData       = await _pool.ExecuteAsync(ProductionHelpers.BuildBomRequest(new BomQuery { Material = body.Material }), ct);
        var bomComponents = ProductionHelpers.ParseBomRows(bomData)
            .Select(r => PerformanceHelpers.NormaliseMaterial(r.Component))
            .Distinct()
            .ToArray();
        var traceMaterials = (body.TraceabilityMaterials ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        var bomMismatch    = traceMaterials.Count > 0
            && !traceMaterials.All(m => bomComponents.Contains(PerformanceHelpers.NormaliseMaterial(m)));

        var packInstruction = ProductionHelpers.BuildPackagingInstruction(body.Customer, body.PackCode);
        var maint = await _pool.ExecuteAsync(
            ProductionHelpers.BuildProdBatchMaintRequest(
                producedBatch.Charge, body.Material, packInstruction, backflush.DocumentNumber, body.WeightKG, body.PackCode), ct);
        var (rcBatch, rcPack) = ProductionHelpers.ParseProdBatchMaintResponse(maint);
        _logger.LogInformation($"Z_ZPRODBATCH_MAINT: batch {producedBatch.Charge} on {backflush.DocumentNumber} || RC_BATCH={rcBatch} RC_PACK={rcPack}");

        return Ok(ApiResponse<DrumBackflushResponse>.Ok(new DrumBackflushResponse
        {
            Backflush          = backflush,
            MaterialDocument   = backflush.DocumentNumber,
            Batch              = producedBatch.Charge,
            RcBatch            = rcBatch,
            RcPack             = rcPack,
            BomMismatch        = bomMismatch,
            ExpectedComponents = bomComponents,
            ActualComponents   = [.. traceMaterials],
        }));
    }


// ── GET /api/production/bom ────────────────────────────────────────
//
// Plain read of ZBOM_INFO (BuildBomRequest/ParseBomRows — the same helper
// DrummingBackflush and PostScrap already use internally) exposed as its
// own endpoint. Needed by Node's drumming flow to work out, ahead of any
// posting, how many metres of a braided (BR) component a drummed
// product's BOM expects per unit — braiding never posts its own SAP
// backflush (unreliable BOM data at that work centre), so Node backflushes
// the exact BOM-implied quantity for that component itself, before the
// drum's own backflush runs. Optional Component filter (IDNRK) narrows to
// one row instead of the whole BOM.

    [HttpGet]

    [Route("bom")]
    public async Task<IHttpActionResult> GetBom(

        [FromBody] BomQuery body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var bomData = await _pool.ExecuteAsync(ProductionHelpers.BuildBomRequest(body), ct);
        var rows    = ProductionHelpers.ParseBomRows(bomData);

        return Ok(ApiResponse<BomRow[]>.Ok(rows));
    }


// ── POST /api/production/scrap/post ──────────────────────────────────

    [HttpGet]

    [Route("check-profit-centre")]
    public async Task<IHttpActionResult> CheckProfitCentre(

        [FromBody] ProfitCentreRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "scrap/post");

        var profitCentreArray = await _pool.ExecuteAsync(ProductionHelpers.BuildProfitCentre(body.Material), ct);
        var profitCentre = ProductionHelpers.ParseSingleSapResult(profitCentreArray);

        return Ok(ApiResponse<String>.Ok(profitCentre));
    }


// ── GET /api/production/check-profit-centres (bulk) ───────────────────
//
// One round trip for every distinct material in a job's BOM, instead of
// one check-profit-centre call per component — used by Normanton-Nexus to
// classify each BOM component as raw material (profit centre 2012 — no
// portal production record exists for these, so traceability there is a
// hand-written SAP batch number rather than something to resolve) vs. a
// portal-tracked semi-finished material.

    [HttpGet]

    [Route("check-profit-centres")]
    public async Task<IHttpActionResult> CheckProfitCentres(

        [FromBody] ProfitCentresRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        if (body.Materials.Count == 0)
            return Ok(ApiResponse<ProfitCentreRow[]>.Ok([]));

        var data = await _pool.ExecuteAsync(ProductionHelpers.BuildProfitCentresRequest(body), ct);
        var rows = ProductionHelpers.ParseProfitCentreRows(data);

        return Ok(ApiResponse<ProfitCentreRow[]>.Ok(rows));
    }


// ── GET /api/production/find-cost-collector ──────────────────────────

    [HttpGet]

    [Route("find-cost-collector")]
    public async Task<IHttpActionResult> FindCostCollector(

        [FromBody] ProfitCentreRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var costCollectorArray = await _pool.ExecuteAsync(ProductionHelpers.BuildCostCollector(body.Material), ct);
        var costCollector = ProductionHelpers.ParseSingleSapResult(costCollectorArray);

        if (string.IsNullOrWhiteSpace(costCollector))
            return Content(HttpStatusCode.BadRequest, ApiResponse<string>.Fail("400", $"No cost collector (AFKO) found for material '{body.Material}'.", null!));

        return Ok(ApiResponse<string>.Ok(costCollector));
    }


// ── POST /api/production/reverse-backflush ──────────────────────────────────

    [HttpPost]

    [Route("reverse-backflush")]
    public async Task<IHttpActionResult> ReverseBackflush(

        [FromBody] Mf41Request body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "reverse-backflush");

        var mf41    = await _pool.ExecuteAsync( ProductionHelpers.BuildMf41Request( body ), ct );
        var response = ProductionHelpers.ParseBdcResponse(mf41);
        _logger.LogInformation("Reversing Backflush: " + body.MaterialDocument + " || " + response.RawMessage);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }



// ── POST /api/production/find-backflush-document ──────────────────────────

    [HttpPost]

    [Route("find-backflush-document")]
    public async Task<IHttpActionResult> FindBackflushDocument(

        [FromBody] FindBackflushDocumentRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var mseg = await _pool.ExecuteAsync(ProductionHelpers.BuildFindBackflushDocumentRequest(body.Batch), ct);
        var row  = ProductionHelpers.ParseBackflushDocumentRows(mseg).FirstOrDefault();

        if (row == null)
            return Content(HttpStatusCode.BadRequest, ApiResponse<BackflushDocumentRow>.Fail("400", $"No backflush (movement 131) found for batch '{body.Batch}'.", null!));

        return Ok(ApiResponse<BackflushDocumentRow>.Ok(row));
    }


// ── POST /api/production/scrap/post ──────────────────────────────────

    [HttpPost]

    [Route("scrap/post")]
    public async Task<IHttpActionResult> PostScrap(

        [FromBody] BomScrapRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "scrap/post");

        var scrapResponses = new BdcWrapper();
        var whmResponses = new TransferOrderWrapper();

        var profitCentreArray = await _pool.ExecuteAsync(ProductionHelpers.BuildProfitCentre(body.Material), ct);
        var profitCentre = ProductionHelpers.ParseSingleSapResult(profitCentreArray);

        var bom    = await _pool.ExecuteAsync(ProductionHelpers.BuildBomRequest(new BomQuery { Material = body.Material }), ct);
        var bomResponse = ProductionHelpers.ParseBomRows(bom);

        if (bomResponse.Length > 0)
            { _logger.LogInformation($"Scrapping {body.Quantity} x {body.Material} - found {bomResponse.Length} components in BOM");  }
        else
            { return Content(HttpStatusCode.BadRequest, ApiResponse<RfcResponse>.Fail("403","No Components in BOM - Unable to Scrap", bom)); }
 
        var kgToUnit = await _pool.ExecuteAsync(ProductionHelpers.BuildKgToUnitRequest(new KgToUnitQuery { Material = body.Material }), ct);
        var kgToUnitResponse = ProductionHelpers.ParseKgToUnit(kgToUnit).FirstOrDefault();

        decimal units = 0;

        try { units = Math.Round(body.Quantity / kgToUnitResponse.KgConversion, 3); }
        catch { return Content(HttpStatusCode.BadRequest, ApiResponse<RfcResponse>.Fail("403","Missing Weight", kgToUnit)); }

        foreach (var row in bomResponse)
        {
            var slocArray = await _pool.ExecuteAsync(ProductionHelpers.BuildStorageLocation(row.Component), ct);
            var sloc = ProductionHelpers.ParseSingleSapResult(slocArray);

            var mb11    = await _pool.ExecuteAsync( ProductionHelpers.BuildBomScrapRequest(
                            new BomScrapRequest { Material = row.Component, Quantity = Math.Round(row.ComponentQty * units, 3), 
                                                  Header = body.Header, MovementType = "551", ScrapReason = body.ScrapReason, 
                                                  StorageLocation = sloc, ProfitCentre = profitCentre, ComponentUnit = row.ComponentUnit 
                                                } ), ct );

            var scrapResponse = ProductionHelpers.ParseBdcResponse(mb11);
            scrapResponses.Responses.Add(scrapResponse);
            _logger.LogInformation($"Posting scrap: {row.Component} x {row.ComponentQty * units} {row.ComponentUnit} from {sloc} || {scrapResponse.RawMessage}");

            if (sloc == "1710" || sloc == "1711") {
                var lt01   = await _pool.ExecuteAsync( WarehouseHelpers.BuildTransferOrderRequest(
                                new CreateTransferOrderRequest  {   StorageLocation = sloc, Material = row.Component,  Quantity = row.ComponentQty * units,
                                    SourceType = "SA", SourceBin = "PTFE", DestinationType = "999", DestinationBin = "SCRAP", } ), ct );
                var whmResponse = WarehouseHelpers.ParseTransferOrderResponse(lt01);
                whmResponses.Responses.Add(whmResponse);
                _logger.LogInformation($"Transfer Order for {row.Component}: {whmResponse.TransferOrderNumber}");
            }
        }

        return Ok(ApiResponse<BdcWrapper>.Ok(scrapResponses));
    }

// ── POST /api/production/mixing-scrap ──────────────────────────────────
//
// Scraps a whole finished mixing batch directly — for tubs that exceed
// their 96h shelf life without being staged into Billet (Normanton-Nexus's
// PROD_SUPERVISOR-approved expiry-scrap action). Unlike PostScrap, which
// fans out over a material's BOM *components* via MB11/BDC, this posts
// once against the mix material itself via BAPI_GOODSMVT_CREATE (movement
// 551, GM_CODE "06") — see MixingScrapHelper.cs for the full rationale:
// this is the same BAPI/GM_CODE combination StockAdjustmentHelper already
// uses and the user has confirmed working (711) against real data on this
// system. No batch/CHARG anywhere: mix materials are not batch-managed in
// SAP — all tub/batch traceability for mixes lives in Normanton-Nexus only.
//
// Like WarehouseController.CreateStockAdjustment, this is a real BAPI and
// needs an explicit BAPI_TRANSACTION_COMMIT/ROLLBACK on the same pinned
// worker afterward, or nothing actually persists in SAP.

    [HttpPost]

    [Route("mixing-scrap")]
    public async Task<IHttpActionResult> PostMixingScrap(

        [FromBody] MixingScrapRequest body,
        [FromUri] bool dryRun = false,
        CancellationToken ct = default)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var storageLocation = body.StorageLocation;
        if (string.IsNullOrWhiteSpace(storageLocation))
        {
            var slocArray = await _pool.ExecuteAsync(ProductionHelpers.BuildStorageLocation(body.Material), ct);
            try { storageLocation = ProductionHelpers.ParseSingleSapResult(slocArray); }
            catch { storageLocation = null; }
        }

        if (string.IsNullOrWhiteSpace(storageLocation))
            return Content(HttpStatusCode.BadRequest, ApiResponse<StockAdjustmentResponse>.Fail("400", $"No storage location (MARC-LGPRO) found for material '{body.Material}'.", null!));

        var request = MixingScrapHelper.BuildMixingScrapRequest(body, storageLocation);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var worker = await _pool.AcquireWorkerAsync(ct);
        try
        {
            var data     = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
            var response = StockAdjustmentHelper.ParseStockAdjustmentResponse(data);

            _logger.LogInformation($"Posting mixing scrap: {body.Material} x {body.Quantity} KG from {storageLocation} || MatDoc {response.MaterialDocument}");

            if (body.TestRun)
            {
                // A test run never creates a real document, so there's nothing
                // to commit — roll back to release whatever SAP locked while
                // simulating the posting.
                await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
                return Ok(ApiResponse<StockAdjustmentResponse>.Ok(response));
            }

            if (!response.Success)
            {
                await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
                return Content((HttpStatusCode)422, ApiResponse<StockAdjustmentResponse>.Fail(
                    "422", "SAP rejected the mixing scrap posting. Transaction rolled back.", response));
            }

            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
            return Ok(ApiResponse<StockAdjustmentResponse>.Ok(response));
        }
        finally
        {
            await _pool.ReleaseWorkerAsync(worker);
        }
    }


// ── POST /api/production/goods-movement-backflush ────────────────────────
//
// Normanton-Nexus's concession path: when a job's traceability was
// approved to proceed despite not matching this material's BOM, this posts
// every component explicitly (correct ones included, not just the
// substituted one) via BAPI_GOODSMVT_CREATE, instead of the normal
// automatic ZF40N backflush. See GoodsMovementHelper.cs/GoodsMovementRequest
// (ProductionModels.cs) for the full rationale and the "UNCONFIRMED for
// this use case — verify via test.http" caveat this carries.
//
// Like PostMixingScrap, this is a real BAPI and needs an explicit
// BAPI_TRANSACTION_COMMIT/ROLLBACK on the same pinned worker afterward, or
// nothing actually persists in SAP.

    [HttpPost]

    [Route("goods-movement-backflush")]
    public async Task<IHttpActionResult> PostGoodsMovementBackflush(

        [FromBody] GoodsMovementRequest body,
        [FromUri] bool dryRun = false,
        CancellationToken ct = default)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        // Components used to carry [MinLength(1)] on the model, but net48's
        // System.ComponentModel.DataAnnotations.MinLengthAttribute.IsValid casts
        // straight to Array — List<T> isn't one, so every real call (any non-
        // empty Components list) crashed with InvalidCastException before this
        // action was ever entered, confirmed live. Checked explicitly here
        // instead, same pattern as PicksheetMaterials/PicksheetStock's own
        // Count == 0 guards.
        if (body.Components.Count == 0)
            return Content(HttpStatusCode.BadRequest, ApiResponse<GoodsMovementResponse>.Fail(
                "INVALID_DATA", "Components must not be empty.", new GoodsMovementResponse { Success = false }));

        // Resolve a storage location (MARC-LGPRO) for any component that
        // didn't already carry one from the BOM snapshot Node sent —
        // mirrors PostMixingScrap's single-material fallback, just once per
        // distinct material across every component line.
        var resolvedStorageLocations = new Dictionary<string, string>();
        foreach (var material in body.Components.Select(c => c.Material).Distinct())
        {
            if (!string.IsNullOrWhiteSpace(body.Components.First(c => c.Material == material).StorageLocation))
                continue;
            try
            {
                var slocArray = await _pool.ExecuteAsync(ProductionHelpers.BuildStorageLocation(material), ct);
                var sloc = ProductionHelpers.ParseSingleSapResult(slocArray);
                if (!string.IsNullOrWhiteSpace(sloc)) resolvedStorageLocations[material] = sloc;
            }
            catch { /* leave unresolved — BuildGoodsMovementRequest omits STGE_LOC and lets SAP reject it with a real message if it's required */ }
        }

        var request = GoodsMovementHelper.BuildGoodsMovementRequest(body, resolvedStorageLocations);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var worker = await _pool.AcquireWorkerAsync(ct);
        try
        {
            var data     = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
            var response = GoodsMovementHelper.ParseGoodsMovementResponse(data);

            _logger.LogInformation($"Concession goods movement for {body.Material} ({body.Header}): {body.Components.Count} component(s) || MatDoc {response.MaterialDocument}");

            if (body.TestRun)
            {
                await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
                return Ok(ApiResponse<GoodsMovementResponse>.Ok(response));
            }

            if (!response.Success)
            {
                await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
                return Content((HttpStatusCode)422, ApiResponse<GoodsMovementResponse>.Fail(
                    "422", "SAP rejected the concession goods movement. Transaction rolled back.", response));
            }

            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
            return Ok(ApiResponse<GoodsMovementResponse>.Ok(response));
        }
        finally
        {
            await _pool.ReleaseWorkerAsync(worker);
        }
    }


// ── POST /api/production/scrap/reverse ──────────────────────────────────

    [HttpPost]

    [Route("scrap/reverse")]
    public async Task<IHttpActionResult> ReverseScrap(

        [FromBody] Mf41Request body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "scrap/reverse");

        var whmResponses = new TransferOrderWrapper();

        var mbst    = await _pool.ExecuteAsync( ProductionHelpers.BuildMbstRequest( body ), ct );
        var response = ProductionHelpers.ParseBdcResponse(mbst);
        _logger.LogInformation($"Reversing Scrap: {body.MaterialDocument} || {response.RawMessage}");

        var matDocData = await _pool.ExecuteAsync( ProductionHelpers.BuildMatDocRequest( body.MaterialDocument ), ct );
        var matDoc = ProductionHelpers.ParseMaterialDocument(matDocData).FirstOrDefault();

        if (matDoc == null) {
            return Content(HttpStatusCode.BadRequest, ApiResponse<BdcResponse>.Fail("403",response.RawMessage,response));
        }

        if (matDoc.StorageLocation == "1710" || matDoc.StorageLocation == "1711") {
            var lt01Data = new CreateTransferOrderRequest  {   StorageLocation = matDoc.StorageLocation, Material = matDoc.Material,  Quantity = matDoc.Quantity,
                                SourceType = "999", SourceBin = "SCRAP", DestinationType = "SA", DestinationBin = "PTFE", };

            var lt01   = await _pool.ExecuteAsync( WarehouseHelpers.BuildTransferOrderRequest( lt01Data ), ct );

            var whmResponse = WarehouseHelpers.ParseTransferOrderResponse(lt01);
            whmResponses.Responses.Add(whmResponse);
            _logger.LogInformation($"Transfer Order for {matDoc.Material}: {whmResponse.TransferOrderNumber}");
        }

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }


// ── GET /api/production/order-text/{salesDocument}/{item} ────────────────
//
// Live RFC_READ_TEXT lookup for the Drumming Ticket's Special Instructions
// section — process-critical, so this always hits SAP directly rather than
// reading a cached/synced table. textId defaults to "004" (special
// instructions) via a query string override (?textId=) if ever needed.

    [HttpGet]

    [Route("order-text/{salesDocument}/{item}")]
    public async Task<IHttpActionResult> GetOrderText(
        string salesDocument,
        string item,
        [FromUri] string? textId = null,
        CancellationToken ct = default)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(
            ProductionHelpers.BuildOrderTextRequest(
                salesDocument, item,
                string.IsNullOrWhiteSpace(textId) ? ProductionHelpers.SpecialInstructionsTextId : textId),
            ct);

        return Ok(ApiResponse<string>.Ok(ProductionHelpers.ParseOrderText(response)));
    }

}
