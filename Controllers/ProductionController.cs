using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client.NativeInterop;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/production")]
public sealed class ProductionController : SapControllerBase
{
    public ProductionController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<ProductionController> logger)
        : base(pool, permissions, logger) { }

    // ── POST /api/production/backflush ──────────────────────────────────

    [HttpPost("backflush")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> Backflush(

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

// ── POST /api/production/scrap/post ──────────────────────────────────

    [HttpGet("check-profit-centre")]
    [ProducesResponseType(typeof(ApiResponse<BdcWrapper>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> CheckProfitCentre(

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



// ── POST /api/production/reverse-backflush ──────────────────────────────────

    [HttpPost("reverse-backflush")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ReverseBackflush(

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

    [HttpPost("find-backflush-document")]
    [ProducesResponseType(typeof(ApiResponse<BackflushDocumentRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> FindBackflushDocument(

        [FromBody] FindBackflushDocumentRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var mseg = await _pool.ExecuteAsync(ProductionHelpers.BuildFindBackflushDocumentRequest(body.Batch), ct);
        var row  = ProductionHelpers.ParseBackflushDocumentRows(mseg).FirstOrDefault();

        if (row == null)
            return BadRequest(ApiResponse<BackflushDocumentRow>.Fail("400", $"No backflush (movement 131) found for batch '{body.Batch}'.", null!));

        return Ok(ApiResponse<BackflushDocumentRow>.Ok(row));
    }


// ── POST /api/production/scrap/post ──────────────────────────────────

    [HttpPost("scrap/post")]
    [ProducesResponseType(typeof(ApiResponse<BdcWrapper>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> PostScrap(

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
            { return BadRequest(ApiResponse<RfcResponse>.Fail("403","No Components in BOM - Unable to Scrap", bom)); }
 
        var kgToUnit = await _pool.ExecuteAsync(ProductionHelpers.BuildKgToUnitRequest(new KgToUnitQuery { Material = body.Material }), ct);
        var kgToUnitResponse = ProductionHelpers.ParseKgToUnit(kgToUnit).FirstOrDefault();

        decimal units = 0;

        try { units = Math.Round(body.Quantity / kgToUnitResponse.KgConversion, 3); }
        catch { return BadRequest(ApiResponse<RfcResponse>.Fail("403","Missing Weight", kgToUnit)); }

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

// ── POST /api/production/scrap/reverse ──────────────────────────────────

    [HttpPost("scrap/reverse")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ReverseScrap(

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
            return BadRequest(ApiResponse<BdcResponse>.Fail("403",response.RawMessage,response));
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



}
