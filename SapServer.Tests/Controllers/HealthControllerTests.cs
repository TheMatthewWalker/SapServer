using System.Net;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

/// <summary>
/// Drives the real OWIN pipeline (TestServer), same as RfcControllerTests -
/// the point of /health is specifically that it works without a bearer
/// token, unlike every SapControllerBase-derived controller.
/// </summary>
public class HealthControllerTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public HealthControllerTests(SapServerTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_200_with_no_bearer_token()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"healthy\"", body);
    }
}
