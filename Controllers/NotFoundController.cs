using System.Net;
using System.Web.Http;
using SapServer.Models;

namespace SapServer.Controllers;

/// <summary>
/// Catches every request that doesn't match any real attribute route — see
/// the catch-all MapHttpRoute registered after MapHttpAttributeRoutes() in
/// Startup.cs. Deliberately does NOT inherit SapControllerBase (whose
/// class-level [Authorize] would otherwise turn a wrong URL into a 401
/// instead of a clean 404) and needs no SAP connection, same reasoning as
/// HealthController.
///
/// Added after confirming for real, more than once, that a request to a
/// route with genuinely no attribute-route match at all crashes the OWIN
/// pipeline under real IIS hosting — System.InvalidOperationException from
/// Microsoft.Owin.Host.SystemWeb.IntegratedPipeline.IntegratedPipelineContext
/// .PushLastObjects — instead of cleanly 404ing. First hit on GET /health
/// before that route existed (fixed by adding it); then again on POST
/// /api/production/label, a route this app has never implemented. Adding a
/// single missing route each time it's hit doesn't scale — every URL Web
/// API's own routing can possibly receive needs to match SOMETHING, so this
/// catch-all guarantees that instead of chasing one path at a time. The
/// deeper "why does a genuinely unmatched attribute route crash
/// IntegratedPipelineContext" mechanism itself is still not fully
/// root-caused, but no request should ever reach that code path again with
/// this in place.
/// </summary>
public sealed class NotFoundController : ApiController
{
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")]
    public IHttpActionResult Handle() =>
        Content(HttpStatusCode.NotFound,
            ApiResponse<object>.Fail("NOT_FOUND", $"No route matched '{Request.RequestUri?.AbsolutePath}'.", null!));
}
