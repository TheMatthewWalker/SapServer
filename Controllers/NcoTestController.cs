using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Services.Nco;

namespace SapServer.Controllers;

/// <summary>
/// SAP NCo rebuild spike — see CLAUDE.md's "SAP NCo Spike" section. Exists to
/// validate connect, concurrent-request handling, and resource cleanup under
/// NCo before committing to a full migration off the SAPFunctions64 COM
/// interop. Deliberately not wired into SapDepartmentPermissions yet (no
/// CheckPermissionAsync call) — gated on [Authorize] (a valid JWT, or dev
/// bypass) only, same baseline every other controller requires.
/// </summary>
[ApiController]
[Authorize]
[Route("api/nco-test")]
public sealed class NcoTestController : ControllerBase
{
    private readonly INcoRfcService _nco;

    public NcoTestController(INcoRfcService nco) => _nco = nco;

    // ── GET /api/nco-test/material/{material} ───────────────────────────────
    // Connect + single ZRFC_READ_TABLES call + parse, end to end.
    [HttpGet("material/{material}")]
    [ProducesResponseType(typeof(ApiResponse<string[][]>), 200)]
    public async Task<IActionResult> GetMaterial(string material, CancellationToken ct)
    {
        var response = await _nco.ExecuteAsync(NcoReadTablesHelper.BuildMaterialLookupRequest(material), ct);
        var rows = NcoReadTablesHelper.ParseMaterialLookupRows(response);
        return Ok(ApiResponse<string[][]>.Ok(rows.ToArray()));
    }

    // ── GET /api/nco-test/concurrency-check?count=5 ──────────────────────────
    // Fires `count` RFC_PING calls concurrently through the same NCo
    // destination and reports each call's wall-clock time. Cross-reference
    // against NcoRfcService's per-call thread-id log lines to confirm calls
    // actually overlap on different threads rather than serializing behind
    // one connection — the NCo analogue of proving SapConnectionPool's
    // SelectWorker round-robin actually spreads load, minus any STA thread
    // pool to route across (NCo needs none).
    [HttpGet("concurrency-check")]
    [ProducesResponseType(typeof(ApiResponse<object>), 200)]
    public async Task<IActionResult> ConcurrencyCheck([FromQuery] int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 20);
        var overall = System.Diagnostics.Stopwatch.StartNew();

        var calls = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _nco.ExecuteAsync(new RfcRequest { FunctionName = "RFC_PING" }, ct);
            sw.Stop();
            return new { Call = i, ElapsedMs = sw.ElapsedMilliseconds };
        }));

        overall.Stop();
        return Ok(ApiResponse<object>.Ok(new { TotalElapsedMs = overall.ElapsedMilliseconds, Calls = calls }));
    }
}
