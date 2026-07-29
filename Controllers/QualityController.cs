using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/quality")]
public sealed class QualityController : SapControllerBase
{
    public QualityController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<QualityController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/quality/display ──────────────────────────────────────────────

    [HttpGet("display")]
    [ProducesResponseType(typeof(ApiResponse<StockRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetBlockedStock([FromQuery] StockQuery query, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), QualityHelpers.FnReadTables, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "display");

        var response = await _pool.ExecuteAsync(QualityHelpers.BuildBlockedStockRequest(query), ct);
        return Ok(ApiResponse<StockRow[]>.Ok(QualityHelpers.ParseBlockedStockRows(response)));
    }


    // ── POST /api/quality/block ──────────────────────────────────

    [HttpPost("block")]
    [ProducesResponseType(typeof(ApiResponse<QualityMb1bResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> BlockStock(
        [FromBody] QualityMb1bRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), QualityHelpers.FnBlockStock, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "block");

        var mb1b   = await _pool.ExecuteAsync(QualityHelpers.BuildMb1bBlockedRequest(body, "BLOCK"),          ct);

        if (body.StorageLocation == "1710" || body.StorageLocation == "1711")
        {
            var (blocked, unrestricted) = QualityHelpers.PrepTransferOrderRequest(body, "BLOCK");

            var binError = await CheckDestinationBinsAsync(blocked, unrestricted, ct);
            if (binError is not null) return binError;

            var whmBlocked = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(blocked),   ct);
            var whmUnrestricted    = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(unrestricted),      ct);
            return Ok(ApiResponse<QualityMb1bResponse>.Ok(
                QualityHelpers.ParseQualityResponse(mb1b, whmBlocked, whmUnrestricted)));
        }
        else
        {
            var whmBlocked = new RfcResponse(); // empty response for non-whm locations
            var whmUnrestricted = new RfcResponse(); // empty response for non-whm locations
            return Ok(ApiResponse<QualityMb1bResponse>.Ok(
                QualityHelpers.ParseQualityResponse(mb1b, whmBlocked, whmUnrestricted)));
        };
    }


    // ── POST /api/quality/unblock ──────────────────────────────────

    [HttpPost("unblock")]
    [ProducesResponseType(typeof(ApiResponse<QualityMb1bResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> UnblockStock(
        [FromBody] QualityMb1bRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), QualityHelpers.FnBlockStock, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "unblock");

        var mb1b   = await _pool.ExecuteAsync(QualityHelpers.BuildMb1bBlockedRequest(body, "UNBLOCK"),          ct);

        if (body.StorageLocation == "1710" || body.StorageLocation == "1711")
        {
            var (blocked, unrestricted) = QualityHelpers.PrepTransferOrderRequest(body, "UNBLOCK");

            var binError = await CheckDestinationBinsAsync(blocked, unrestricted, ct);
            if (binError is not null) return binError;

            var whmBlocked = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(blocked),   ct);
            var whmUnrestricted    = await _pool.ExecuteAsync(WarehouseHelpers.BuildTransferOrderRequest(unrestricted),      ct);
            return Ok(ApiResponse<QualityMb1bResponse>.Ok(
                QualityHelpers.ParseQualityResponse(mb1b, whmBlocked, whmUnrestricted)));
        }
        else
        {
            var whmBlocked = new RfcResponse(); // empty response for non-whm locations
            var whmUnrestricted = new RfcResponse(); // empty response for non-whm locations
            return Ok(ApiResponse<QualityMb1bResponse>.Ok(
                QualityHelpers.ParseQualityResponse(mb1b, whmBlocked, whmUnrestricted)));
        };
    }

    // Checks both transfer orders' destination bins exist (LAGP) before either
    // is sent to L_TO_CREATE_SINGLE — same WarehouseHelpers.BuildBinCheckRequest/
    // BinExists check used by WarehouseController.CreateTransferOrder, and for
    // the same reason: an invalid bin fails that RFC ungracefully (no real SAP
    // error message, connection torn down and needing to reconnect) rather
    // than with a clean business error. One of the two destinations here is
    // always the fixed 922/BLOCK bin (which should always exist), but it's
    // simplest and cheapest to check both rather than work out which one is
    // user-supplied for BLOCK vs UNBLOCK. Returns null if both bins exist, or
    // an UnprocessableEntity result to return directly if either is missing.
    private async Task<IActionResult?> CheckDestinationBinsAsync(
        CreateTransferOrderRequest req1, CreateTransferOrderRequest req2, CancellationToken ct)
    {
        foreach (var req in new[] { req1, req2 })
        {
            // Same fix as WarehouseController.CreateTransferOrder: pad the
            // user-typed bin (BLOCK is a fixed literal and already >= 10
            // chars, so SapPad.Pad leaves it alone either way) before the
            // LAGP existence check, not just inside BuildTransferOrderRequest
            // further down — otherwise an unpadded numeric bin here false-
            // negatives the same way it did for the plain transfer-order flow.
            var destinationBin = SapPad.Pad(req.DestinationBin, 10);

            var binCheck = await _pool.ExecuteAsync(
                WarehouseHelpers.BuildBinCheckRequest(req.DestinationType, destinationBin), ct);

            if (!WarehouseHelpers.BinExists(binCheck))
            {
                var msg = $"Destination bin {req.DestinationType}/{destinationBin} does not exist in SAP warehouse {WarehouseHelpers.Warehouse}. Check the storage type and bin and try again.";
                return UnprocessableEntity(ApiResponse<QualityMb1bResponse>.Fail("422", msg, new QualityMb1bResponse()));
            }
        }
        return null;
    }
}