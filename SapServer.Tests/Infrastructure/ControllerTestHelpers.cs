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

    // Web API 2's Ok(x) and Content(status, x) return two DIFFERENT sibling
    // types — OkNegotiatedContentResult<T> and NegotiatedContentResult<T> —
    // not a base/derived pair as originally assumed here (confirmed the hard
    // way: every AssertOk call against a real Ok(x) result threw
    // "OkNegotiatedContentResult<T> has no StatusCode property" once this ran
    // against the real System.Web.Http assembly in CI). OkNegotiatedContentResult<T>
    // has no StatusCode property at all — it implicitly always means 200, so
    // that case is special-cased below instead of read via reflection.
    // Both types do carry .Content, which ContentOf reads via reflection —
    // asserting on the exact closed generic type at ~150 call sites across
    // these test files would mean hand-supplying the precise ApiResponse<T>
    // for every assertion instead.
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
        var type = result.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Web.Http.Results.OkNegotiatedContentResult<>))
            return HttpStatusCode.OK;

        var prop = type.GetProperty("StatusCode")
            ?? throw new InvalidOperationException($"{type} has no StatusCode property — not a NegotiatedContentResult<T>?");
        return (HttpStatusCode)prop.GetValue(result)!;
    }

    private static object? ContentOf(IHttpActionResult result)
    {
        var prop = result.GetType().GetProperty("Content")
            ?? throw new InvalidOperationException($"{result.GetType()} has no Content property — not a NegotiatedContentResult<T>?");
        return prop.GetValue(result);
    }
}
