using System.Data;
using Microsoft.AspNetCore.Mvc;
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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "backflush");

        var charge = await _pool.ExecuteAsync(ProductionHelpers.BuildRequiresCharge(body.Material), ct);
        var zf40n    = await _pool.ExecuteAsync(
            ProductionHelpers.BuildZf40nRequest(
                body,
                ProductionHelpers.ParseRequiresCharge(charge)),
            ct);
        var response = ProductionHelpers.ParseBdcResponse(zf40n);
        Console.WriteLine("Backflushing: " + body.Material + " x " + body.Quantity + " || " + response.RawMessage);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "reverse-backflush");

        var mf41    = await _pool.ExecuteAsync( ProductionHelpers.BuildMf41Request( body ), ct );
        var response = ProductionHelpers.ParseBdcResponse(mf41);
        Console.WriteLine("Reversing: " + body.MaterialDocument + " || " + response.RawMessage);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "scrap/post");

        var scrapResponses = new BdcWrapper();
        var whmResponses = new TransferOrderWrapper();

        var profitCentreArray = await _pool.ExecuteAsync(ProductionHelpers.BuildProfitCentre(body.Material), ct);
        var profitCentre = ProductionHelpers.ParseSingleSapResult(profitCentreArray);

        var bom    = await _pool.ExecuteAsync(ProductionHelpers.BuildBomRequest(new BomQuery { Material = body.Material }), ct);
        var bomResponse = ProductionHelpers.ParseBomRows(bom);
        Console.WriteLine($"Scrapping {body.Quantity} x {body.Material} - found {bomResponse.Length} components in BOM");
 
        foreach (var row in bomResponse)
        {
            var slocArray = await _pool.ExecuteAsync(ProductionHelpers.BuildStorageLocation(row.Component), ct);
            var sloc = ProductionHelpers.ParseSingleSapResult(slocArray);

            var mb11    = await _pool.ExecuteAsync( ProductionHelpers.BuildBomScrapRequest(
                            new BomScrapRequest { Material = row.Component, Quantity = Math.Round(row.ComponentQty * body.Quantity, 3), Header = body.Header,
                                MovementType = "551", ScrapReason = body.ScrapReason, StorageLocation = sloc, ProfitCentre = profitCentre } ), ct );

            var scrapResponse = ProductionHelpers.ParseBdcResponse(mb11);
            scrapResponses.Responses.Add(scrapResponse);
            Console.WriteLine($"Posting scrap: {row.Component} x {row.ComponentQty * body.Quantity} {row.ComponentUnit} from {sloc} || {scrapResponse.RawMessage}");

            var lt01   = await _pool.ExecuteAsync( WarehouseHelpers.BuildTransferOrderRequest(
                            new CreateTransferOrderRequest  {   StorageLocation = sloc, Material = row.Component,  Quantity = row.ComponentQty * body.Quantity,
                                SourceType = "SA", SourceBin = "PTFE", DestinationType = "999", DestinationBin = "SCRAP", } ), ct );

            var whmResponse = WarehouseHelpers.ParseTransferOrderResponse(lt01);
            whmResponses.Responses.Add(whmResponse);
            Console.WriteLine($"Transfer Order for {row.Component}: {whmResponse.TransferOrderNumber}");
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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "scrap/reverse");

        var whmResponses = new TransferOrderWrapper();

        var mbst    = await _pool.ExecuteAsync( ProductionHelpers.BuildMbstRequest( body ), ct );
        var response = ProductionHelpers.ParseBdcResponse(mbst);
        Console.WriteLine("Reversing: " + body.MaterialDocument + " || " + response.RawMessage);

        var matDocData = await _pool.ExecuteAsync( ProductionHelpers.BuildMatDocRequest( body.MaterialDocument ), ct );
        var matDoc = ProductionHelpers.ParseMaterialDocument(matDocData).FirstOrDefault();

        if (matDoc == null)
            throw new Exception("Material document not found.");

        var lt01Data = new CreateTransferOrderRequest  {   StorageLocation = matDoc.StorageLocation, Material = matDoc.Material,  Quantity = matDoc.Quantity,
                            SourceType = "999", SourceBin = "SCRAP", DestinationType = "SA", DestinationBin = "PTFE", };

        var lt01   = await _pool.ExecuteAsync( WarehouseHelpers.BuildTransferOrderRequest( lt01Data ), ct );

        var whmResponse = WarehouseHelpers.ParseTransferOrderResponse(lt01);
        whmResponses.Responses.Add(whmResponse);
        Console.WriteLine($"Transfer Order for {matDoc.Material}: {whmResponse.TransferOrderNumber}");

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }



}
