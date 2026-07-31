using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

/// <summary>
/// Engineering > Packaging Data — ported from 363_new_packaging.xlsm.
///   - Mass Packaging Update: bulk-set the plant-default packaging instruction's
///     assigned material for a list of parts.
///   - New Packaging Creation: create the MM01 material masters + CS01 BOMs for
///     a customer part's packaging materials (10 codes: SD/MD/LD/XD/SB/MB/LB/XB/C1/C2).
///   - Packaging Instruction Detail: read/save the full ZPACK_INSTR row for a
///     material (plant-default or per-customer), including quantities and the
///     trace-charge-id flag.
/// </summary>
[Route("api/packaging")]
public sealed class PackagingController : SapControllerBase
{
    private readonly SapPoolOptions _poolOptions;

    public PackagingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        IOptions<SapPoolOptions> poolOptions,
        ILogger<PackagingController> logger)
        : base(pool, permissions, logger)
    {
        _poolOptions = poolOptions.Value;
    }

// ── GET /api/packaging/{material}/exists ──────────────────────────────────

    [HttpGet("{material}/exists")]
    [ProducesResponseType(typeof(ApiResponse<bool>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MaterialExists(string material, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildMaterialExistsRequest(material), ct);
        return Ok(ApiResponse<bool>.Ok(PackagingHelpers.ParseMaterialExists(response)));
    }

// ── GET /api/packaging/{material}/description ─────────────────────────────

    [HttpGet("{material}/description")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MaterialDescription(string material, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildMaktRequest(material), ct);
        return Ok(ApiResponse<string>.Ok(PackagingHelpers.ParseSingleValue(response)));
    }

// ── GET /api/packaging/{material}/mara ─────────────────────────────────────

    [HttpGet("{material}/mara")]
    [ProducesResponseType(typeof(ApiResponse<PackagingMaraRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MaterialDetails(string material, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildMaraRequest(material), ct);
        var row = PackagingHelpers.ParseMara(response);

        if (row == null)
            return BadRequest(ApiResponse<PackagingMaraRow>.Fail("400", $"Material '{material}' not found (MARA).", null!));

        return Ok(ApiResponse<PackagingMaraRow>.Ok(row));
    }

// ── GET /api/packaging/{material}/bom ──────────────────────────────────────

    [HttpGet("{material}/bom")]
    [ProducesResponseType(typeof(ApiResponse<PackagingBomRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MaterialBom(string material, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildPackagingBomRequest(material), ct);
        return Ok(ApiResponse<PackagingBomRow[]>.Ok(PackagingHelpers.ParsePackagingBom(response)));
    }

// ── GET /api/packaging/{material}/customers ────────────────────────────────

    [HttpGet("{material}/customers")]
    [ProducesResponseType(typeof(ApiResponse<PackagingCustomerRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MaterialCustomers(string material, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildCustomerListRequest(material), ct);
        return Ok(ApiResponse<PackagingCustomerRow[]>.Ok(PackagingHelpers.ParseCustomerList(response)));
    }

// ── GET /api/packaging/{material}/instruction?customer= ───────────────────
// Customer omitted/blank = the plant-default (KUNNR blank) row.

    [HttpGet("{material}/instruction")]
    [ProducesResponseType(typeof(ApiResponse<PackagingInstrRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<IActionResult> GetInstruction(string material, [FromQuery] string? customer, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildZpackInstrRequest(material, customer ?? ""), ct);
        var row = PackagingHelpers.ParseZpackInstr(response);

        if (row == null)
            return NotFound(ApiResponse<PackagingInstrRow>.Fail("404",
                string.IsNullOrWhiteSpace(customer)
                    ? $"No plant-default packaging instruction found for '{material}'."
                    : $"No packaging instruction found for '{material}' / customer '{customer}'.", null!));

        return Ok(ApiResponse<PackagingInstrRow>.Ok(row));
    }

// ── PUT /api/packaging/instruction ─────────────────────────────────────────
// Packaging Instruction Detail ("Dialog") save — full row write (insert or
// update). Trace flags (incl. the required "charge/trace ID" flag) only ever
// apply at plant-default level; PackagingHelpers silently drops them for a
// customer-specific row rather than writing them somewhere they don't apply.

    [HttpPut("instruction")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> SaveInstruction([FromBody] PackagingInstrSaveRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(PackagingHelpers.BuildPackInstrMaintRequest(body), ct);
        var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(response);

        _logger.LogInformation($"Packaging instruction {body.SqlAction} for {body.Material}/{body.Customer ?? "(plant)"} || {message}");

        if (!success)
            return UnprocessableEntity(ApiResponse<string>.Fail("422", message, null!));

        return Ok(ApiResponse<string>.Ok(message));
    }

// ── DELETE /api/packaging/instruction ──────────────────────────────────────

    [HttpDelete("instruction")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> DeleteInstruction([FromBody] PackagingInstrSaveRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var response = await _pool.ExecuteAsync(
            PackagingHelpers.BuildPackInstrMaintRequest(new PackagingInstrSaveRequest
            {
                Material  = body.Material,
                Customer  = body.Customer,
                SqlAction = "D",
            }), ct);

        var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(response);

        _logger.LogInformation($"Packaging instruction DELETE for {body.Material}/{body.Customer ?? "(plant)"} || {message}");

        if (!success)
            return UnprocessableEntity(ApiResponse<string>.Fail("422", message, null!));

        return Ok(ApiResponse<string>.Ok(message));
    }

// ── POST /api/packaging/mass-update ────────────────────────────────────────
// Mass Packaging Update — bulk-set the plant-default packaging instruction's
// assigned material (PALL_MATNR) for a list of parts. Reads each material's
// existing plant-default row first and only changes PALL_MATNR, so quantities
// and flags already set (batch spread, trace charge id, etc.) are preserved.

    [HttpPost("mass-update")]
    [ProducesResponseType(typeof(ApiResponse<List<MassPackagingUpdateResult>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> MassUpdate([FromBody] MassPackagingUpdateRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var results = new List<MassPackagingUpdateResult>();

        foreach (var row in body.Rows)
        {
            var existingResponse = await _pool.ExecuteAsync(PackagingHelpers.BuildZpackInstrRequest(row.Material, ""), ct);
            var existing = PackagingHelpers.ParseZpackInstr(existingResponse);

            if (existing == null)
            {
                results.Add(new MassPackagingUpdateResult
                {
                    Material = row.Material,
                    Success  = false,
                    Message  = "No existing plant-default packaging instruction — use Packaging Instruction Detail to create one first.",
                });
                continue;
            }

            var saveRequest = new PackagingInstrSaveRequest
            {
                Material     = row.Material,
                Customer     = null,
                SqlAction    = "U",
                PackMaterial = row.PackMaterial,
                PalletQty    = existing.PalletQty,
                SmallBoxQty  = existing.SmallBoxQty,
                PackProd     = existing.PackProd,
                BoxGen       = existing.BoxGen,
                BatchSpread  = existing.BatchSpread,
                PartMix      = existing.PartMix,
                ChargeReq    = existing.ChargeReq,
                TechStatReq  = existing.TechStatReq,
                PNumReq      = existing.PNumReq,
            };

            var updateResponse = await _pool.ExecuteAsync(PackagingHelpers.BuildPackInstrMaintRequest(saveRequest), ct);
            var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(updateResponse);

            _logger.LogInformation($"Mass packaging update: {row.Material} -> {row.PackMaterial} || {message}");
            results.Add(new MassPackagingUpdateResult { Material = row.Material, Success = success, Message = message });
        }

        return Ok(ApiResponse<List<MassPackagingUpdateResult>>.Ok(results));
    }

// ── POST /api/packaging/create ─────────────────────────────────────────────
// Legacy service-account path — kept for parity with the rest of this
// controller's endpoints, but no longer called by Node for the real New
// Packaging Creation flow (see CreateElevated below): the service account
// doesn't have (and isn't being given) MM01/CS01 create authorization, same
// reasoning as PurchasingController's plain create-po vs create-po-elevated.

    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResponse<List<CreatePackagingResult>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> Create([FromBody] CreatePackagingRequest body, CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PackagingHelpers.FnCreate, ct);

        var results = await RunCreateFlow(body.CustomerPart, body.Codes, req => _pool.ExecuteAsync(req, ct), ct);
        return Ok(ApiResponse<List<CreatePackagingResult>>.Ok(results));
    }

// ── POST /api/packaging/create-elevated ────────────────────────────────────
// New Packaging Creation — per-user-elevated flow: logon with the calling
// user's own SAP credentials -> create the MM01 material master + CS01 BOM
// for each selected packaging-type code -> logoff, all on one pinned
// elevated worker slot (see SapConnectionPool.AcquireElevatedWorkerAsync /
// ReleaseElevatedWorkerAsync). Matches PurchasingController's
// create-po-elevated pattern: the shared service account doesn't have (and
// isn't being given) rights to create material masters, so this has to run
// as a real user with real SAP authorization instead.
//
// Deliberately does NOT call CheckPermissionAsync — same reasoning as
// PurchasingController's elevated endpoints: this database can't reliably be
// reached over the required TLS version from here, so authorization is
// enforced twice elsewhere instead — Node's own MASTER_DATA permission check
// before it ever calls this endpoint, and SAP's own real per-user
// authorization on MM01/CS01 itself now that this genuinely runs as the
// calling user.
//
// The elevated worker is always released in a finally block regardless of
// how far the flow got, so a slot can never be left logged in as one user
// waiting to be handed to somebody else. Unlike PO creation, there's no
// BAPI commit/rollback step here — MM01/CS01 batch-input transactions
// commit per-screen as they run, not as a separate BAPI_TRANSACTION_COMMIT.

    [HttpPost("create-elevated")]
    [ProducesResponseType(typeof(ApiResponse<List<CreatePackagingResult>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    public async Task<IActionResult> CreateElevated([FromBody] CreatePackagingElevatedRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SapUsername) || string.IsNullOrWhiteSpace(body.SapPassword))
            return BadRequest(ApiResponse<List<CreatePackagingResult>>.Fail(
                "MISSING_CREDENTIALS", "SAP username and password are required for this elevated action.", null!));

        if (string.IsNullOrWhiteSpace(body.CustomerPart))
            return BadRequest(ApiResponse<List<CreatePackagingResult>>.Fail(
                "NO_CUSTOMER_PART", "customerPart is required.", null!));

        var elevatedCreds = new SapConnectionOptions
        {
            System   = _poolOptions.ServiceAccount.System,
            Client   = _poolOptions.ServiceAccount.Client,
            SystemId = _poolOptions.ServiceAccount.SystemId,
            User     = body.SapUsername,
            Password = body.SapPassword,
            Language = _poolOptions.ServiceAccount.Language,
        };

        var worker = await _pool.AcquireElevatedWorkerAsync(elevatedCreds, ct);
        try
        {
            var results = await RunCreateFlow(body.CustomerPart, body.Codes,
                req => _pool.ExecuteOnWorkerAsync(worker, req, ct), ct);
            return Ok(ApiResponse<List<CreatePackagingResult>>.Ok(results));
        }
        finally
        {
            await _pool.ReleaseElevatedWorkerAsync(worker);
        }
    }

// ── Shared New Packaging Creation loop ─────────────────────────────────────
// Same per-code MM01+CS01 logic for both the service-account and elevated
// paths above — only how each RFC call is dispatched differs (a plain pooled
// worker vs. a pinned elevated one), so that's passed in as a delegate
// rather than duplicated.

    private async Task<List<CreatePackagingResult>> RunCreateFlow(
        string customerPart, List<string> codes, Func<RfcRequest, Task<RfcResponse>> execute, CancellationToken ct)
    {
        var effectiveCodes = codes.Count > 0 ? codes : PackagingHelpers.PackagingCodes.ToList();
        var results = new List<CreatePackagingResult>();

        foreach (var code in effectiveCodes)
        {
            if (!PackagingHelpers.TryGetDrumComponent(code, out var component))
            {
                results.Add(new CreatePackagingResult { Code = code, Message = $"Unknown packaging code '{code}'." });
                continue;
            }

            var material = PackagingHelpers.PackagingMaterial(customerPart, code);

            var existsResponse = await execute(PackagingHelpers.BuildMaterialExistsRequest(material));
            if (PackagingHelpers.ParseMaterialExists(existsResponse))
            {
                results.Add(new CreatePackagingResult
                {
                    Code = code, Material = material, AlreadyExisted = true,
                    Message = "Material already exists — skipped.",
                });
                continue;
            }

            var mm01Response = await execute(
                PackagingHelpers.BuildCreateMaterialRequest(material, PackagingHelpers.ReferenceMaterial(code)));
            var mm01Result = ProductionHelpers.ParseBdcResponse(mm01Response);

            _logger.LogInformation($"New Packaging: create material {material} (ref {PackagingHelpers.ReferenceMaterial(code)}) || {mm01Result.RawMessage}");

            if (mm01Result.Type == "E" || mm01Result.Type == "A")
            {
                results.Add(new CreatePackagingResult
                {
                    Code = code, Material = material, MaterialCreated = false,
                    Message = $"MM01 failed: {mm01Result.Message}",
                });
                continue;
            }

            var cs01Response = await execute(PackagingHelpers.BuildCreateBomRequest(material, component));
            var cs01Result = ProductionHelpers.ParseBdcResponse(cs01Response);

            _logger.LogInformation($"New Packaging: create BOM {material} <- {component} || {cs01Result.RawMessage}");

            var bomOk = !(cs01Result.Type == "E" || cs01Result.Type == "A");

            results.Add(new CreatePackagingResult
            {
                Code            = code,
                Material        = material,
                MaterialCreated = true,
                BomCreated      = bomOk,
                Message         = bomOk
                    ? "Material and BOM created."
                    : $"Material created, but BOM (CS01) failed: {cs01Result.Message}",
            });
        }

        return results;
    }
}
