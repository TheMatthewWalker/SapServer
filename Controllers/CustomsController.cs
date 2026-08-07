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
        var kna1Rows  = CustomsHelpers.ParseKna1Rows(response);

        // Also pull KNVV (customer master sales data) for Incoterms — best-effort:
        // if this call fails for any reason, still return the KNA1 rows rather than
        // failing the whole customer auto-create flow over a field that's always
        // been manually editable anyway.
        try
        {
            var knvvRequest  = CustomsHelpers.BuildKnvvRequest(request);
            var knvvResponse = await _pool.ExecuteAsync(knvvRequest, ct);
            var incoterms    = CustomsHelpers.ParseKnvvIncoterms(knvvResponse);

            kna1Rows = kna1Rows
                .Select(r => r with { Incoterms = incoterms.GetValueOrDefault(r.CustomerCode, "") })
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KNVV Incoterms lookup failed for KNA1 customer batch — returning KNA1 rows without Incoterms.");
        }

        return Ok(ApiResponse<Kna1Row[]>.Ok(kna1Rows));
    }

    // No CheckPermissionAsync gate here, matching this controller's other four
    // endpoints (lips/likp/vbfa/marc/kna1) and the same precedent documented in
    // WarehouseController's picksheet-stock and ConsignmentController: these are
    // only ever called via the Node-side shared service token (userId 0), so
    // there's no per-user identity for PermissionService to check — the real
    // gate is requirePermission('LOG_CUSTOMS_REPORT') on the Normanton-Nexus route.
    [HttpPost("vbrk")]
    [ProducesResponseType(typeof(ApiResponse<VbrkRow[]>), 200)]
    public async Task<IActionResult> Vbrk([FromBody] VbrkRequest request, CancellationToken ct)
    {
        if (request.Invoices.Count == 0)
            return Ok(ApiResponse<VbrkRow[]>.Ok([]));

        var rfcRequest = CustomsHelpers.BuildVbrkRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<VbrkRow[]>.Ok(CustomsHelpers.ParseVbrkRows(response)));
    }

    // Same no-CheckPermissionAsync precedent as this controller's other endpoints
    // (see the comment above Vbrk) — service-token-only call.
    //
    // Consignment-customer fallback: used when VBFA has no billing document for
    // a delivery line (goods shipped without a commercial invoice). Looks up a
    // customs sales price via SAP's standard pricing-condition tables instead —
    // see CustomsHelpers.BuildConsignmentPriceRequest for the full rationale.
    [HttpPost("consignment-price")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentPriceRow[]>), 200)]
    public async Task<IActionResult> ConsignmentPrice([FromBody] ConsignmentPriceRequest request, CancellationToken ct)
    {
        if (request.Lines.Count == 0)
            return Ok(ApiResponse<ConsignmentPriceRow[]>.Ok([]));

        var rfcRequest = CustomsHelpers.BuildConsignmentPriceRequest(request);

        var response = await _pool.ExecuteAsync(rfcRequest, ct);
        return Ok(ApiResponse<ConsignmentPriceRow[]>.Ok(CustomsHelpers.ParseConsignmentPriceRows(response)));
    }

}
