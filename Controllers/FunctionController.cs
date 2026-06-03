using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/function")]
public sealed class FunctionController : SapControllerBase
{
    public FunctionController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<FunctionController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/function/params ──────────────────────────────────────────────

    [HttpGet("params")]
    [ProducesResponseType(typeof(ApiResponse<FunctionParams[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetFunctionParams([FromBody] FunctionQuery body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), FunctionHelper.FnGetFunction, ct);

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "function-viewer");

        var response = await _pool.ExecuteAsync(FunctionHelper.BuildFunctionViewer(body.FunctionName), ct);
        var parameters = FunctionHelper.ParseFunctionViewer(response);

        foreach (var param in parameters)
        {
            if (string.IsNullOrEmpty(param.ParamType))
                continue;

            var fieldResponse = await _pool.ExecuteAsync(FunctionHelper.BuildFunctionFields(param.ParamType), ct);
            var fields = FunctionHelper.ParseFunctionFields(fieldResponse);
            param.Fields = fields;
        }

        return Ok(ApiResponse<FunctionParams[]>.Ok(parameters));
    }




}
