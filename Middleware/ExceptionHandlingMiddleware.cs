using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Middleware;

/// <summary>
/// Catches exceptions thrown anywhere in the pipeline and maps them to
/// consistent JSON error responses using the ApiResponse envelope.
/// Keeps controllers thin — they never need try/catch.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            await HandleAsync(ctx, ex);
        }
    }

    private async Task HandleAsync(HttpContext ctx, Exception ex)
    {
        var (statusCode, errorCode, message) = ex switch
        {
            SapPermissionException    => (403, "FORBIDDEN",       ex.Message),
            SapConnectionException    => (503, "SAP_UNAVAILABLE", "The SAP system is currently unavailable. Please try again shortly."),
            SapExecutionException e   => (422, "RFC_ERROR",       e.SapMessage ?? e.Message),
            PoolExhaustedException    => (503, "POOL_EXHAUSTED",  "All SAP workers are busy. Please retry your request."),
            OperationCanceledException=> (499, "REQUEST_CANCELLED","The request was cancelled."),
            UnauthorizedAccessException => (401, "UNAUTHORIZED",  "Authentication is required."),
            _                         => (500, "INTERNAL_ERROR",  "An unexpected error occurred.")
        };

        if (statusCode >= 500)
            _logger.LogError(ex, "Unhandled exception (HTTP {Status}).", statusCode);
        else
            _logger.LogWarning("Handled exception [{Code}]: {Message}", errorCode, ex.Message);

        ctx.Response.StatusCode  = statusCode;
        ctx.Response.ContentType = "application/json";

        var body = ApiResponse<object>.Fail(errorCode, message);
        await ctx.Response.WriteAsJsonAsync(body, ctx.RequestAborted);
    }
}
