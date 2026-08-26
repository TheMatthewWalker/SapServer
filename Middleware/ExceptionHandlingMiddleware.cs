using System.Text.Json;
using Microsoft.Owin;
using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Middleware;

/// <summary>
/// Catches exceptions thrown anywhere in the OWIN pipeline and maps them to
/// consistent JSON error responses using the ApiResponse envelope. Ported
/// from the ASP.NET Core RequestDelegate/HttpContext middleware shape to
/// OWIN's OwinMiddleware/IOwinContext — the exception→status/code mapping
/// itself is unchanged.
/// </summary>
public sealed class ExceptionHandlingMiddleware : OwinMiddleware
{
    private readonly ILogger _logger;

    public ExceptionHandlingMiddleware(OwinMiddleware next, ILogger logger) : base(next)
    {
        _logger = logger;
    }

    public override async Task Invoke(IOwinContext context)
    {
        try
        {
            await Next.Invoke(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(IOwinContext context, Exception ex)
    {
        var (statusCode, errorCode, message) = ex switch
        {
            SapPermissionException      => (403, "FORBIDDEN",        ex.Message),
            SapConnectionException      => (503, "SAP_UNAVAILABLE",  "The SAP system is currently unavailable. Please try again shortly."),
            SapExecutionException e     => (422, "RFC_ERROR",        string.IsNullOrEmpty(e.SapMessage) ? e.Message : e.SapMessage),
            PoolExhaustedException      => (503, "POOL_EXHAUSTED",   "All SAP workers are busy. Please retry your request."),
            OperationCanceledException  => (499, "REQUEST_CANCELLED","The request was cancelled."),
            UnauthorizedAccessException => (401, "UNAUTHORIZED",     "Authentication is required."),
            _                           => (500, "INTERNAL_ERROR",   "An unexpected error occurred.")
        };

        if (statusCode >= 500)
            _logger.LogError(ex, "Unhandled exception (HTTP {Status}).", statusCode);
        else
            _logger.LogWarning("Handled exception [{Code}]: {Message}", errorCode, ex.Message);

        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/json";

        var safeError = new
        {
            ExceptionType = ex.GetType().Name,
            Message = ex.Message
        };

        var body = ApiResponse<object>.Fail(errorCode, message, safeError);
        await context.Response.WriteAsync(JsonSerializer.Serialize(body), context.Request.CallCancelled);
    }
}
