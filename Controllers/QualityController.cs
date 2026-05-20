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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "display");

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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "block");

        var mb1b   = await _pool.ExecuteAsync(QualityHelpers.BuildMb1bBlockedRequest(body, "BLOCK"),          ct);

        if (body.StorageLocation == "1710" || body.StorageLocation == "1711") 
        {
            var (blocked, unrestricted) = QualityHelpers.PrepTransferOrderRequest(body, "BLOCK");
            var whmBlocked = await _pool.ExecuteAsync(QualityHelpers.BuildTransferOrderRequest(blocked),   ct);
            var whmUnrestricted    = await _pool.ExecuteAsync(QualityHelpers.BuildTransferOrderRequest(unrestricted),      ct);
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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "unblock");

        var mb1b   = await _pool.ExecuteAsync(QualityHelpers.BuildMb1bBlockedRequest(body, "UNBLOCK"),          ct);

        if (body.StorageLocation == "1710" || body.StorageLocation == "1711") 
        {
            var (blocked, unrestricted) = QualityHelpers.PrepTransferOrderRequest(body, "UNBLOCK");
            var whmBlocked = await _pool.ExecuteAsync(QualityHelpers.BuildTransferOrderRequest(blocked),   ct);
            var whmUnrestricted    = await _pool.ExecuteAsync(QualityHelpers.BuildTransferOrderRequest(unrestricted),      ct);
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


}