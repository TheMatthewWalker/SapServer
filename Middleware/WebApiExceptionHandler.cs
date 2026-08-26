using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using SapServer.Exceptions;

namespace SapServer.Middleware;

/// <summary>
/// The Web-API-2-native exception hook — see SapExceptionMapper's doc
/// comment for why this, not ExceptionHandlingMiddleware, is what actually
/// handles almost every real exception in this app. Web API 2's HttpServer
/// catches any exception raised during controller construction, filters, or
/// action execution itself and converts it to a response internally before
/// it can ever reach the surrounding OWIN pipeline; registering this class
/// as the app's IExceptionHandler (see Startup.ConfigurePipeline) is the
/// supported way to control what that response looks like instead of Web
/// API's own generic '{"Message":"An error has occurred."}' 500.
/// </summary>
public sealed class WebApiExceptionHandler : ExceptionHandler
{
    private readonly ILogger _logger;

    public WebApiExceptionHandler(ILogger<WebApiExceptionHandler> logger)
    {
        _logger = logger;
    }

    public override void Handle(ExceptionHandlerContext context)
    {
        var ex = context.Exception;
        var (statusCode, errorCode, message) = SapExceptionMapper.Map(ex);

        if (statusCode >= 500)
            _logger.LogError(ex, "Unhandled exception (HTTP {Status}).", statusCode);
        else
            _logger.LogWarning("Handled exception [{Code}]: {Message}", errorCode, ex.Message);

        var body = SapExceptionMapper.BuildBody(ex, errorCode, message);
        context.Result = new JsonExceptionResult(context.Request, (HttpStatusCode)statusCode, body);
    }

    private sealed class JsonExceptionResult : IHttpActionResult
    {
        private readonly HttpRequestMessage _request;
        private readonly HttpStatusCode _statusCode;
        private readonly object _body;

        public JsonExceptionResult(HttpRequestMessage request, HttpStatusCode statusCode, object body)
        {
            _request    = request;
            _statusCode = statusCode;
            _body       = body;
        }

        public Task<HttpResponseMessage> ExecuteAsync(CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                RequestMessage = _request,
                Content = new StringContent(
                    JsonSerializer.Serialize(_body, SapExceptionMapper.JsonOptions),
                    Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
