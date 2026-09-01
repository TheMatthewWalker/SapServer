using System.Text.Json;
using SapServer.Models;

namespace SapServer.Exceptions;

/// <summary>
/// Single source of truth for exception -> (HTTP status, error code, safe
/// message) mapping, shared by both places an unhandled exception can
/// actually be caught in this app:
///
///   - Middleware/ExceptionHandlingMiddleware (OWIN-level) — only ever sees
///     an exception thrown OUTSIDE Web API's own pipeline (CORS/JWT
///     middleware itself); genuinely rare in practice.
///   - Middleware/WebApiExceptionHandler (System.Web.Http.ExceptionHandling.
///     IExceptionHandler) — Web API 2's HttpServer catches every unhandled
///     exception raised during controller construction/filter/action
///     execution ITSELF and converts it to a response before it can ever
///     propagate back out to the surrounding OWIN pipeline, so this is the
///     path that actually handles the vast majority of real exceptions
///     (SapPermissionException, SapConnectionException, SapExecutionException,
///     PoolExhaustedException, etc. are all thrown from within a controller
///     action or a helper it calls). Without this second handler, every one
///     of those was silently replaced by Web API's own generic
///     '{"Message":"An error has occurred."}' 500 response — discovered via
///     SapServer.Tests/Controllers' real OWIN TestServer pipeline tests.
/// </summary>
internal static class SapExceptionMapper
{
    // System.Text.Json.JsonSerializer.Serialize defaults to PascalCase
    // (matching the C# property names exactly) unless told otherwise — a
    // default ASP.NET Core's MVC pipeline applied automatically that neither
    // of this app's two exception-handling paths inherit for free.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (int StatusCode, string ErrorCode, string Message) Map(Exception ex) => ex switch
    {
        SapPermissionException      => (403, "FORBIDDEN",        ex.Message),
        SapConnectionException      => (503, "SAP_UNAVAILABLE",  "The SAP system is currently unavailable. Please try again shortly."),
        SapExecutionException e     => (422, "RFC_ERROR",        string.IsNullOrEmpty(e.SapMessage) ? e.Message : e.SapMessage),
        PoolExhaustedException      => (503, "POOL_EXHAUSTED",   "All SAP workers are busy. Please retry your request."),
        OperationCanceledException  => (499, "REQUEST_CANCELLED","The request was cancelled."),
        UnauthorizedAccessException => (401, "UNAUTHORIZED",     "Authentication is required."),
        _                           => (500, "INTERNAL_ERROR",   "An unexpected error occurred.")
    };

    public static ApiResponse<object> BuildBody(Exception ex, string errorCode, string message) =>
        ApiResponse<object>.Fail(errorCode, message, new { ExceptionType = ex.GetType().Name, Message = ex.Message });
}
