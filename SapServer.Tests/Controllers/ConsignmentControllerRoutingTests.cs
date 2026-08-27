using System.Net;
using Moq;
using SapServer.Models;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

// Confirmed for real against a live IIS deploy (2026-08-27): GetVendorGr's
// sinceDate parameter, even though it's already string?, made Web API 2's
// [FromUri] binder 404 the ENTIRE request whenever a caller omitted it from
// the query string — Normanton-Nexus's daily cron and manual "Sync GR from
// SAP" button both omit it on every normal call, so this broke ALL
// consignment GR syncing the moment the ASP.NET-Core-to-WebAPI2 rebuild
// reached production. See ConsignmentController.cs's GetVendorGr comment.
//
// This needs the real TestServer pipeline, not the direct-Moq-instantiation
// pattern SmallControllersTests.cs's ConsignmentControllerTests uses — a
// Moq-constructed controller never goes through Web API's actual [FromUri]
// route binding at all, which is exactly why this class of regression stays
// invisible until a real deploy (see CLAUDE.md's dryRun-gotcha entry for the
// same lesson on a value-type parameter).
public class ConsignmentControllerRoutingTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public ConsignmentControllerRoutingTests(SapServerTestFactory factory)
    {
        _factory = factory;
        _factory.PoolMock.Reset();
        _factory.PermissionsMock.Reset();
    }

    [Fact]
    public async Task GetVendorGr_with_no_sinceDate_in_the_query_string_still_routes_and_returns_200()
    {
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse());

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.GetAsync("/api/consignment/gr?sapVendorNumber=0000200604");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetVendorGr_still_honours_an_explicit_sinceDate_when_given()
    {
        RfcRequest? captured = null;
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RfcRequest, CancellationToken>((r, _) => captured ??= r)
            .ReturnsAsync(new RfcResponse());

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.GetAsync("/api/consignment/gr?sapVendorNumber=0000200604&sinceDate=01.01.2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var whereText = string.Join(" ", captured!.InputTablesItems["where_clause"].Select(row => row["TEXT"]));
        Assert.Contains("MKPF~BUDAT GE '01.01.2026'", whereText);
    }
}
