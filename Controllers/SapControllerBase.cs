using System.Web.Http;
using SapServer.Exceptions;
using SapServer.Services.Interfaces;
using System.Security.Claims;

namespace SapServer.Controllers;

[Authorize]
public abstract class SapControllerBase : ApiController
{
    protected readonly ISapConnectionPool _pool;
    protected readonly IPermissionService _permissions;
    protected readonly ILogger            _logger;

    protected SapControllerBase(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger            logger)
    {
        _pool        = pool;
        _permissions = permissions;
        _logger      = logger;
    }

    protected int GetUserId()
    {
        // System.Web.Http.ApiController.User is IPrincipal — see RfcController.
        // GetUserId's identical comment (that controller doesn't derive from
        // this base, so it needs its own copy of this cast).
        var principal = (ClaimsPrincipal)User;
        var claim = principal.FindFirst("userId")
                 ?? principal.FindFirst(ClaimTypes.NameIdentifier)
                 ?? throw new UnauthorizedAccessException("JWT is missing the 'userId' claim.");

        if (!int.TryParse(claim.Value, out int userId))
            throw new UnauthorizedAccessException(
                $"'userId' claim value '{claim.Value}' is not a valid integer.");

        return userId;
    }

    protected async Task CheckPermissionAsync(int userId, string functionName, CancellationToken ct)
    {
        if (!await _permissions.CanExecuteAsync(userId, functionName, ct))
            throw new SapPermissionException(functionName);
    }
}
