using System.Net;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

/// <summary>
/// Drives the real OWIN pipeline (TestServer), same as HealthControllerTests
/// - the point of the catch-all route is specifically that Web API's real
/// routing sees it, which a direct Moq-instantiated controller test can't
/// exercise at all.
/// </summary>
public class NotFoundControllerTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public NotFoundControllerTests(SapServerTestFactory factory) => _factory = factory;

    [Fact]
    public async Task An_unmatched_GET_route_returns_a_clean_404_envelope()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/production/label");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":false", body);
        Assert.Contains("\"code\":\"NOT_FOUND\"", body);
    }

    [Fact]
    public async Task An_unmatched_POST_route_returns_a_clean_404_envelope()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/production/label", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
