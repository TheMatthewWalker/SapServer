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

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "cost-sheet");

        var request = CostingHelper.BuildCostSheetRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var response = await _pool.ExecuteAsync(request, ct);
        return Ok(ApiResponse<CostSheetRow[]>.Ok(CostingHelper.ParseCostSheetRows(response)));
    }


    [HttpPost("period-balance")]
    [ProducesResponseType(typeof(ApiResponse<List<PeriodBalanceRow>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetPeriodBalance(
        [FromBody] PeriodBalanceRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnPeriodBalances, ct);

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "period-balance");

        var tasks = body.GlAccounts.Select(async acct =>
        {
            var build = CostingHelper.BuildPeriodBalances(body, acct);
            var data = await _pool.ExecuteAsync(build, ct);

            return CostingHelper.ParsePeriodBalances(
                data,
                body.PeriodFrom,
                body.PeriodTo
            );
        });

        var results = await Task.WhenAll(tasks);
        var response = results.SelectMany(x => x).ToList();
    
        return Ok(ApiResponse<List<PeriodBalanceRow>>.Ok(response.ToList()));
    }



    [HttpPost("profit-center")]
    [ProducesResponseType(typeof(ApiResponse<ProfitCenterRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetProfitCenter(
        [FromBody] ProfitCenterRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "profit-center");

        var request = CostingHelper.BuildProfitCenterRequest(body);
        var data = await _pool.ExecuteAsync(request, ct);
        var response = CostingHelper.ParseProfitCenterRows(data);

        return Ok(ApiResponse<ProfitCenterRow[]>.Ok(response));
    }


    [HttpPost("freight-posting")]
    [ProducesResponseType(typeof(ApiResponse<FreightPostingRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> PostFreight(
        [FromBody] FreightPostingRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        _logger.LogInformation(
        "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "freight-posting");

        var worker = _pool.AcquireWorker();

        var request = CostingHelper.BuildFreightPostingRequest(body, "");
        var data = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
        var response = CostingHelper.ParseFreightPostingRows(data);

        if (string.IsNullOrEmpty(response.AccountingNumber))
        {
            var rollback = await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return BadRequest(ApiResponse<FreightPostingRow>.Fail("INVALID_DATA", "Freight posting failed. Transaction rolled back.", response));
        }

        var commit = await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
        return Ok(ApiResponse<FreightPostingRow>.Ok(response));
    }



    [HttpPost("freight-posting-batch")]
    public async Task<IActionResult> PostFreightBatch(
        [FromBody] List<FreightPostingRequest> requests,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        _logger.LogInformation(
            "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "freight-posting-batch");

        var results = new List<FreightPostingRow>();

        // Limit to your COM pool size (3)
        var semaphore = new SemaphoreSlim(3);

        var tasks = requests.Select(async request =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var rfcRequest = CostingHelper.BuildFreightPostingRequest(request, "");
                var data = await _pool.ExecuteAsync(rfcRequest, ct);
                var parsed = CostingHelper.ParseFreightPostingRows(data);

                lock (results) // protect shared list
                { results.AddRange(parsed); }
            }
            finally
            { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return Ok(ApiResponse<List<FreightPostingRow>>.Ok(results));
    }



}
