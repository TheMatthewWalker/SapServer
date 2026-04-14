using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/costing")]
public sealed class CostingController : SapControllerBase
{
    public CostingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<CostingController> logger)
        : base(pool, permissions, logger) { }

    [HttpPost("cost-sheet")]
    [ProducesResponseType(typeof(ApiResponse<CostSheetRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetCostSheet(
        [FromBody] CostSheetRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        var request = CostingHelper.BuildCostSheetRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var response = await _pool.ExecuteAsync(request, ct);
        return Ok(ApiResponse<CostSheetRow[]>.Ok(CostingHelper.ParseCostSheetRows(response)));
    }
}
