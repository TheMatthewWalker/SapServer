using System.Net;
using System.Web.Http;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[RoutePrefix("api/costing")]
public sealed class CostingController : SapControllerBase
{
    public CostingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<CostingController> logger)
        : base(pool, permissions, logger) { }


    [HttpPost]


    [Route("cost-sheet")]
    public async Task<IHttpActionResult> GetCostSheet(
        [FromBody] CostSheetRequest body,
        [FromUri] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "cost-sheet");

        var request = CostingHelper.BuildCostSheetRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var response = await _pool.ExecuteAsync(request, ct);
        return Ok(ApiResponse<CostSheetRow[]>.Ok(CostingHelper.ParseCostSheetRows(response)));
    }


    [HttpPost]


    [Route("period-balance")]
    public async Task<IHttpActionResult> GetPeriodBalance(
        [FromBody] PeriodBalanceRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnPeriodBalances, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "period-balance");

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



    [HttpPost]



    [Route("profit-center")]
    public async Task<IHttpActionResult> GetProfitCenter(
        [FromBody] ProfitCenterRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "profit-center");

        var request = CostingHelper.BuildProfitCenterRequest(body);
        var data = await _pool.ExecuteAsync(request, ct);
        var response = CostingHelper.ParseProfitCenterRows(data);

        return Ok(ApiResponse<ProfitCenterRow[]>.Ok(response));
    }


    [HttpPost]


    [Route("freight-posting")]
    public async Task<IHttpActionResult> PostFreight(
        [FromBody] FreightPostingRequest body,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        //_logger.LogInformation(
        //"User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "freight-posting");

        var worker = _pool.AcquireWorker();

        var request = CostingHelper.BuildFreightPostingRequest(body, "");
        var data = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
        var response = CostingHelper.ParseFreightPostingRows(data);

        if (string.IsNullOrEmpty(response.AccountingNumber))
        {
            var rollback = await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return Content(HttpStatusCode.BadRequest, ApiResponse<FreightPostingRow>.Fail("INVALID_DATA", "Freight posting failed. Transaction rolled back.", response));
        }

        var commit = await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
        return Ok(ApiResponse<FreightPostingRow>.Ok(response));
    }



    [HttpPost]



    [Route("freight-posting-batch")]
    public async Task<IHttpActionResult> PostFreightBatch(
        [FromBody] List<FreightPostingRequest> requests,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), CostingHelper.FnReadTables, ct);

        //_logger.LogInformation(
        //    "User {UserId} executing ENDPOINT '{endpoint}'.", GetUserId(), "freight-posting-batch");

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
                { results.Add(parsed); } // ParseFreightPostingRows returns one row, not a collection
            }
            finally
            { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return Ok(ApiResponse<List<FreightPostingRow>>.Ok(results));
    }



}
