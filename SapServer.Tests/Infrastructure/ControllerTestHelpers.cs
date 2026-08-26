using System.Net;
using System.Security.Claims;
using System.Web.Http;

namespace SapServer.Tests.Infrastructure;

/// <summary>
/// Direct-instantiation controller tests (vs. the full OWIN TestServer
/// HTTP-pipeline tests already covering RfcController) — faster to write per
/// controller, still exercises the same permission checks, pool calls, and
/// response shaping, just without the real authentication/authorization/
/// routing pipeline around it (already proven separately).
/// </summary>
public static class ControllerTestHelpers
{
    public static void SetUser(ApiController controller, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("userId", userId.ToString()) });
        controller.User = new ClaimsPrincipal(identity);
    }

    // Web API 2's Ok(x)/Content(status, x) all return one generic family
    // (OkNegotiatedContentResult<T> : NegotiatedContentResult<T>) instead of
    // ASP.NET Core's distinct OkObjectResult/BadRequestObjectResult/etc. types
    // — asserting on the exact closed generic type at ~150 call sites across
    // these test files would mean hand-supplying the precise ApiResponse<T>
    // for every assertion. These helpers assert on .StatusCode and return
    // .Content via reflection instead, working for any T without needing it
    // named at the call site — test-only code, so the reflection cost is fine.
    public static object? AssertOk(IHttpActionResult result) => AssertStatus(result, HttpStatusCode.OK);
    public static object? AssertBadRequest(IHttpActionResult result) => AssertStatus(result, HttpStatusCode.BadRequest);
    public static object? AssertNotFound(IHttpActionResult result) => AssertStatus(result, HttpStatusCode.NotFound);
    public static object? AssertUnprocessableEntity(IHttpActionResult result) => AssertStatus(result, (HttpStatusCode)422);

    public static object? AssertStatus(IHttpActionResult result, HttpStatusCode expected)
    {
        Assert.Equal(expected, StatusOf(result));
        return ContentOf(result);
    }

    private static HttpStatusCode StatusOf(IHttpActionResult result)
    {
        var prop = result.GetType().GetProperty("StatusCode")
            ?? throw new InvalidOperationException($"{result.GetType()} has no StatusCode property — not a NegotiatedContentResult<T>?");
        return (HttpStatusCode)prop.GetValue(result)!;
    }

    private static object? ContentOf(IHttpActionResult result)
    {
        var prop = result.GetType().GetProperty("Content")
            ?? throw new InvalidOperationException($"{result.GetType()} has no Content property — not a NegotiatedContentResult<T>?");
        return prop.GetValue(result);
    }
}
