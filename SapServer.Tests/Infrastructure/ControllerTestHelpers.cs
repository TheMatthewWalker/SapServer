using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SapServer.Tests.Infrastructure;

/// <summary>
/// Direct-instantiation controller tests (vs. the full WebApplicationFactory
/// HTTP-pipeline tests already covering RfcController/ProductionController)
/// — faster to write per controller, still exercises the same permission
/// checks, pool calls, and response shaping, just without the real
/// authentication/authorization/routing pipeline around it (already proven
/// separately).
/// </summary>
public static class ControllerTestHelpers
{
    public static void SetUser(ControllerBase controller, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("userId", userId.ToString()) });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }
}
