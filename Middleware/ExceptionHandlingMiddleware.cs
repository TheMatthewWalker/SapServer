using System.Text.Json;
using Microsoft.Owin;
using SapServer.Exceptions;

namespace SapServer.Middleware;

/// <summary>
/// Catches exceptions thrown anywhere in the OWIN pipeline OUTSIDE Web API's
/// own request handling (e.g. from CORS or JWT bearer middleware itself) and
/// maps them to the same JSON error envelope WebApiExceptionHandler produces
/// for exceptions thrown inside a controller action — see
/// SapExceptionMapper's doc comment for why there are two of these.
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
        var (statusCode, errorCode, message) = SapExceptionMapper.Map(ex);

        if (statusCode >= 500)
            _logger.LogError(ex, "Unhandled exception (HTTP {Status}) on {Method} {Path}.",
                statusCode, context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning("Handled exception [{Code}] on {Method} {Path}: {Message}",
                errorCode, context.Request.Method, context.Request.Path, ex.Message);

        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/json";

        var body = SapExceptionMapper.BuildBody(ex, errorCode, message);
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, SapExceptionMapper.JsonOptions), context.Request.CallCancelled);
    }
}
