using System.Security.Claims;
using System.Web.Http;
using SapServer.Exceptions;
using SapServer.Models;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

/// <summary>
/// Exposes SAP RFC execution over HTTP.
///
/// Authentication: JWT Bearer token issued by sql2005-bridge.
/// Authorization:  per-function permissions stored in dbo.SapDepartmentPermissions.
/// </summary>
[RoutePrefix("api/rfc")]
[Authorize]
public sealed class RfcController : ApiController
{
    private readonly ISapConnectionPool _pool;
    private readonly IPermissionService _permissions;
    private readonly ILogger<RfcController> _logger;

    public RfcController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<RfcController> logger)
    {
        _pool        = pool;
        _permissions = permissions;
        _logger      = logger;
    }

    /// <summary>
    /// Execute an RFC function call on an available STA pool worker.
    /// The caller specifies which export parameters and output tables to read back.
    /// </summary>
    /// <remarks>
    /// Example request body:
    /// <code>
    /// {
    ///   "functionName": "L_TO_CREATE_SINGLE",
    ///   "importParameters": {
    ///     "I_LGNUM": "001",
    ///     "I_MATNR": "000000000012345678",
    ///     "I_ANFME": "10"
    ///   },
    ///   "exportParameters": ["E_TANUM"],
    ///   "outputTables": {
    ///     "RETURN": ["TYPE", "MESSAGE", "NUMBER"]
    ///   }
    /// }
    /// </code>
    /// </remarks>
    [HttpPost]
    [Route("execute")]
    public async Task<IHttpActionResult> Execute(
        [FromBody] RfcRequest request,
        CancellationToken cancellationToken)
    {
        int userId = GetUserId();

        if (!await _permissions.CanExecuteAsync(userId, request.FunctionName, cancellationToken))
            throw new SapPermissionException(request.FunctionName);

        _logger.LogInformation(
            "User {UserId} executing RFC '{Function}'.", userId, request.FunctionName);

        var response = await _pool.ExecuteAsync(request, cancellationToken);

        return Ok(ApiResponse<RfcResponse>.Ok(response));
    }

    /// <summary>
    /// Returns the live health status of every STA pool worker.
    /// Restricted to admin and superadmin roles.
    /// </summary>
    [HttpGet]
    [Route("status")]
    [Authorize(Roles = "admin,superadmin")]
    public IHttpActionResult Status()
    {
        var status = _pool.GetPoolStatus();
        return Ok(ApiResponse<IReadOnlyList<WorkerStatus>>.Ok(status));
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the userId claim from the JWT. The claim name "userId" matches
    /// what sql2005-bridge embeds when it signs the token.
    /// Falls back to the standard NameIdentifier claim.
    /// </summary>
    private int GetUserId()
    {
        // System.Web.Http.ApiController.User is IPrincipal — JWT claims only
        // reach us via the underlying ClaimsPrincipal Startup.cs's OWIN JWT
        // bearer middleware actually populates, so this cast is required
        // (ASP.NET Core's ControllerBase.User was already typed as
        // ClaimsPrincipal directly).
        var principal = (ClaimsPrincipal)User;
        var claim = principal.FindFirst("userId")
                 ?? principal.FindFirst(ClaimTypes.NameIdentifier)
                 ?? throw new UnauthorizedAccessException("JWT is missing the 'userId' claim.");

        if (!int.TryParse(claim.Value, out int userId))
            throw new UnauthorizedAccessException($"'userId' claim value '{claim.Value}' is not a valid integer.");

        return userId;
    }
}
