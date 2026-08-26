using System.Security.Claims;
using Microsoft.Owin;

namespace SapServer.Services;

/// <summary>
/// Development-only OWIN middleware that auto-authenticates every request as
/// userId=0. Used when Auth:DevBypassAuth = true. Ported from the ASP.NET
/// Core AuthenticationHandler&lt;T&gt; shape to a plain OWIN middleware that
/// sets the OWIN Authentication.User directly — there's no scheme-registry
/// concept to hook into under System.Web.Http/OWIN the way ASP.NET Core's
/// AddAuthentication(...).AddScheme(...) worked, so Startup.cs installs this
/// middleware in place of the JWT bearer middleware entirely when the dev
/// bypass flag is set, rather than registering it as an alternate scheme.
/// </summary>
internal sealed class DevAuthMiddleware : OwinMiddleware
{
    internal const string SchemeName = "DevBypass";

    public DevAuthMiddleware(OwinMiddleware next) : base(next) { }

    public override Task Invoke(IOwinContext context)
    {
        var claims = new[]
        {
            new Claim("userId",        "0"),
            new Claim(ClaimTypes.Name, "dev-bypass"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "superadmin"),
        };

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);

        context.Authentication.User = principal;

        return Next.Invoke(context);
    }
}
