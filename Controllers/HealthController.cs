using System.Web.Http;
using SapServer.Models;

namespace SapServer.Controllers;

/// <summary>
/// Lightweight, unauthenticated liveness check for external monitoring/
/// health-check probes — deliberately does NOT inherit SapControllerBase
/// (whose class-level [Authorize] would otherwise reject an unauthenticated
/// prober) and needs no SAP connection at all. Route is bare ("health", no
/// "api/" prefix) to match what an external prober already expects rather
/// than requiring it be reconfigured.
///
/// Added after confirming for real that a request to a route with no match
/// at all (e.g. a prober hitting this exact path before this controller
/// existed) crashes the OWIN pipeline under real IIS hosting instead of
/// cleanly returning 404 — System.InvalidOperationException from
/// Microsoft.Owin.Host.SystemWeb.IntegratedPipeline.IntegratedPipelineContext
/// .PushLastObjects, caught by ExceptionHandlingMiddleware. The deeper cause
/// of that (Web API's own unmatched-route handling under this OWIN host)
/// isn't fully root-caused, but any route that's genuinely expected to be
/// hit — like a health check — should exist for real rather than relying on
/// Web API's default "no route matched" behavior.
/// </summary>
public sealed class HealthController : ApiController
{
    [HttpGet]
    [Route("health")]
    public IHttpActionResult Get() =>
        Ok(ApiResponse<object>.Ok(new
        {
            status      = "healthy",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            timestampUtc = DateTime.UtcNow
        }));
}
