using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/logistics")]
public sealed class LogisticsController : SapControllerBase
{
    public LogisticsController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<LogisticsController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/logistics/picksheets/open ──────────────────────────────────────────────

    [HttpGet("picksheets/open")]
    [ProducesResponseType(typeof(ApiResponse<PicksheetRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetOpenPicksheets(CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), LogisticsHelpers.FnReadTables, ct);

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "picksheets/open");

        var picksheetRequest = await _pool.ExecuteAsync(LogisticsHelpers.BuildVBUKRequest(), ct);
        var picksheetResponse = LogisticsHelpers.ParseVBUKRows(picksheetRequest);

 
        var rawResponse = await _pool.ExecuteAsync(LogisticsHelpers.BuildPicksheetRequest(picksheetResponse), ct);
        var response = LogisticsHelpers.ParsePicksheetRows(rawResponse);

        //foreach (var row in response)
        //    _logger.LogInformation(
        //        "Picksheet {DeliveryNumber} for customer {CustomerNumber} due on {DueDate} with incoterms {Incoterms}.",
        //        row.DeliveryNumber, row.CustomerNumber, row.DueDate, row.Incoterms);
        
        return Ok(ApiResponse<PicksheetRow[]>.Ok(response));
    }


}
