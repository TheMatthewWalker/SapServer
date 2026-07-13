using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[ApiController]
[Authorize]
[Route("api/customs")]
public sealed class CustomsController : SapControllerBase
{
    private static readonly JsonSerializerOptions _debugJson = new() { WriteIndented = true };
    public CustomsController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<CostingController> logger)
        : base(pool, permissions, logger) { }

    [HttpPost("lips")]
    [ProducesResponseType(typeof(ApiResponse<LipsRow[]>), 200)]
    public async Task<IActionResult> Lips([FromBody] LipsRequest request, CancellationToken ct)
    {
        if (request.Deliveries.Count == 0)
            return Ok(ApiResponse<LipsRow[]>.Ok([]));

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "lips");

        var rfcRequest = CustomsHelpers.BuildLipsRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<LipsRow[]>.Ok(CustomsHelpers.ParseLipsRows(response)));
    }

    [HttpPost("likp")]
    [ProducesResponseType(typeof(ApiResponse<LikpRow[]>), 200)]
    public async Task<IActionResult> Likp([FromBody] LikpRequest request, CancellationToken ct)
    {
        if (request.Deliveries.Count == 0)
            return Ok(ApiResponse<LikpRow[]>.Ok([]));

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "likp");

        var rfcRequest = CustomsHelpers.BuildLikpRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<LikpRow[]>.Ok(CustomsHelpers.ParseLikpRows(response)));
    }

    [HttpPost("vbfa")]
    [ProducesResponseType(typeof(ApiResponse<VbfaRow[]>), 200)]
    public async Task<IActionResult> Vbfa([FromBody] VbfaRequest request, CancellationToken ct)
    {
        if (request.Lines.Count == 0)
            return Ok(ApiResponse<VbfaRow[]>.Ok([]));
            
        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "vbfa");

        var rfcRequest = CustomsHelpers.BuildVbfaRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<VbfaRow[]>.Ok(CustomsHelpers.ParseVbfaRows(response, request)));
    }

    [HttpPost("marc")]
    [ProducesResponseType(typeof(ApiResponse<MarcRow[]>), 200)]
    public async Task<IActionResult> Marc([FromBody] MarcRequest request, CancellationToken ct)
    {
        if (request.Materials.Count == 0)
            return Ok(ApiResponse<MarcRow[]>.Ok([]));

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "marc");

        var rfcRequest = CustomsHelpers.BuildMarcRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<MarcRow[]>.Ok(CustomsHelpers.ParseMarcRows(response)));
    }

    [HttpPost("kna1")]
    [ProducesResponseType(typeof(ApiResponse<Kna1Row[]>), 200)]
    public async Task<IActionResult> Kna1([FromBody] Kna1Request request, CancellationToken ct)
    {
        if (request.Customers.Count == 0)
            return Ok(ApiResponse<Kna1Row[]>.Ok([]));

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "kna1");

        var rfcRequest = CustomsHelpers.BuildKna1Request(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<Kna1Row[]>.Ok(CustomsHelpers.ParseKna1Rows(response)));
    }


}
