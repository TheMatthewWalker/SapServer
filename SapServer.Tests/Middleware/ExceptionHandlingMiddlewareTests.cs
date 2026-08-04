using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SapServer.Exceptions;
using SapServer.Middleware;

namespace SapServer.Tests.Middleware;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int StatusCode, JsonElement Body)> InvokeAsync(Exception thrown)
    {
        var middleware = new ExceptionHandlingMiddleware(_ => throw thrown, NullLogger<ExceptionHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task SapPermissionException_maps_to_403_FORBIDDEN()
    {
        var (status, body) = await InvokeAsync(new SapPermissionException("Z_SOME_RFC"));

        Assert.Equal(403, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("FORBIDDEN", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SapConnectionException_maps_to_503_SAP_UNAVAILABLE_with_a_generic_message()
    {
        var (status, body) = await InvokeAsync(new SapConnectionException(2, "underlying detail the caller should not see"));

        Assert.Equal(503, status);
        Assert.Equal("SAP_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());
        // The generic message is what's exposed, not the raw exception detail.
        Assert.Equal("The SAP system is currently unavailable. Please try again shortly.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SapExecutionException_maps_to_422_RFC_ERROR_and_prefers_the_SAP_message_when_present()
    {
        var (status, body) = await InvokeAsync(new SapExecutionException("ZF40N", "generic failure message", "Material 30005R does not exist"));

        Assert.Equal(422, status);
        Assert.Equal("RFC_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Material 30005R does not exist", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SapExecutionException_falls_back_to_the_generic_message_when_no_SAP_message_was_captured()
    {
        var (status, body) = await InvokeAsync(new SapExecutionException("ZF40N", "generic failure message", sapMessage: null));

        Assert.Equal(422, status);
        Assert.Equal("generic failure message", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task PoolExhaustedException_maps_to_503_POOL_EXHAUSTED()
    {
        var (status, body) = await InvokeAsync(new PoolExhaustedException("all workers busy"));

        Assert.Equal(503, status);
        Assert.Equal("POOL_EXHAUSTED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task OperationCanceledException_maps_to_499_REQUEST_CANCELLED()
    {
        var (status, body) = await InvokeAsync(new OperationCanceledException());

        Assert.Equal(499, status);
        Assert.Equal("REQUEST_CANCELLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnauthorizedAccessException_maps_to_401_UNAUTHORIZED()
    {
        var (status, body) = await InvokeAsync(new UnauthorizedAccessException());

        Assert.Equal(401, status);
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Any_other_exception_maps_to_500_INTERNAL_ERROR_without_leaking_its_message()
    {
        var (status, body) = await InvokeAsync(new InvalidOperationException("some internal detail"));

        Assert.Equal(500, status);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", body.GetProperty("error").GetProperty("message").GetString());
    }
}
