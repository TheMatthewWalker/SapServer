using System.Net;
using System.Net.Http.Json;
using Moq;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services;
using SapServer.Tests.Infrastructure;
using System.Web.Http;

namespace SapServer.Tests.Controllers;

public class ProductionControllerTests : IClassFixture<SapServerTestFactory>
{
    private readonly SapServerTestFactory _factory;

    public ProductionControllerTests(SapServerTestFactory factory)
    {
        _factory = factory;
        _factory.PoolMock.Reset();
        _factory.PermissionsMock.Reset();
    }

    [Fact]
    public async Task FindBackflushDocument_without_the_required_permission_returns_403()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.PoolMock.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FindBackflushDocument_returns_400_when_no_movement_131_row_is_found()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no data_display table at all

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FindBackflushDocument_returns_the_parsed_row_when_permitted_and_found()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sapResponse = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MBLNR|MATNR|MENGE|LGORT" }, // header — skipped
                    new() { ["WA"] = "5000001234|30005R|10.0|1710" },
                },
            },
        };
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sapResponse);

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/find-backflush-document", new FindBackflushDocumentRequest { Batch = "ABC123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BackflushDocumentRow>>();
        Assert.Equal("5000001234", body!.Data!.MaterialDocument);
        Assert.Equal("30005R", body.Data.Material);
        Assert.Equal("1710", body.Data.StorageLocation);
    }

    // PostScrap (BomScrapRequest) had no test coverage at all before this
    // regression pair — added specifically to cover the ScrapReason
    // nullability fix, since a Moq-direct-instantiation test can't exercise
    // real Web API model binding/validation the way this TestServer-based
    // class does.

    [Fact]
    public async Task PostScrap_with_omitted_ScrapReason_passes_validation_and_reaches_business_logic()
    {
        // Regression test: BomScrapRequest.ScrapReason used to be a non-
        // nullable string defaulting to "" — an omitted JSON property bound
        // to "", which failed [StringLength(4, MinimumLength=4)] once
        // ValidateModelAttribute started enforcing it globally, even though
        // Normanton-Nexus always omits this field whenever the real reason
        // code from prod.ScrapReasons isn't exactly 4 characters (routes/
        // productionnexus.js). Now nullable, so an omitted field binds to
        // null, which StringLengthAttribute correctly treats as valid.
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            // profit-centre lookup first — ParseSingleSapResult throws on a
            // genuinely empty response, so this needs one real row.
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = "9912" } } } })
            .ReturnsAsync(new RfcResponse()); // BOM lookup comes back empty -> fast 400 from real business logic

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/scrap/post", new BomScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", MovementType = "551",
            // ScrapReason deliberately omitted
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RfcResponse>>();
        // Reached PostScrap's own "no BOM components" check, not blocked by
        // model validation — proves the omitted ScrapReason was accepted.
        Assert.Equal("No Components in BOM - Unable to Scrap", body!.Error!.Message);
    }

    [Fact]
    public async Task PostScrap_with_a_ScrapReason_of_the_wrong_length_is_still_rejected_by_validation()
    {
        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/scrap/post", new BomScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", MovementType = "551", ScrapReason = "12",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("ScrapReason", body!.Error!.Message);
        _factory.PoolMock.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never); // never reaches business logic
    }

    // PostMixingScrap posts via BAPI_GOODSMVT_CREATE on a pinned worker
    // (AcquireWorker/ExecuteOnWorkerAsync), same convention as
    // WarehouseControllerTests' CreateStockAdjustment coverage — see
    // MixingScrapHelper.cs for why. The storage-location lookup (when not
    // supplied) still goes through the shared ExecuteAsync path, since
    // that's a plain read, not part of the BAPI's transaction.

    [Fact]
    public async Task PostMixingScrap_without_the_required_permission_returns_403()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.PoolMock.Verify(p => p.AcquireWorkerAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostMixingScrap_returns_400_when_no_storage_location_can_be_resolved()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // storage-location lookup comes back empty

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _factory.PoolMock.Verify(p => p.AcquireWorkerAsync(It.IsAny<CancellationToken>()), Times.Never); // never even reaches the BAPI
    }

    [Fact]
    public async Task PostMixingScrap_dryRun_returns_the_built_request_without_acquiring_a_worker()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPRO" }, new() { ["WA"] = "1710" } } } });

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap?dryRun=true", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RfcRequest>>();
        Assert.Equal(StockAdjustmentHelper.FnGoodsMvtCreate, body!.Data!.FunctionName);
        _factory.PoolMock.Verify(p => p.AcquireWorkerAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostMixingScrap_posts_against_the_mix_material_itself_with_no_batch_field_and_commits()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPRO" }, new() { ["WA"] = "1710" } } } });
        _factory.PoolMock
            .Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MATERIALDOCUMENT"] = "5000009999" } });

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<StockAdjustmentResponse>>();
        Assert.Equal("5000009999", body!.Data!.MaterialDocument);
        Assert.True(body.Data.Success);
        _factory.PoolMock.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Once);
        _factory.PoolMock.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostMixingScrap_rolls_back_when_SAP_reports_no_material_document()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPRO" }, new() { ["WA"] = "1710" } } } });
        _factory.PoolMock
            .Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse()); // no MATERIALDOCUMENT -> Success = false

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917",
        });

        Assert.Equal((HttpStatusCode)422, response.StatusCode); // net48's HttpStatusCode enum predates UnprocessableEntity
        _factory.PoolMock.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostMixingScrap_always_rolls_back_a_test_run_even_when_SAP_reports_success()
    {
        _factory.PermissionsMock
            .Setup(p => p.CanExecuteAsync(1, ProductionHelpers.FnCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.PoolMock
            .Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPRO" }, new() { ["WA"] = "1710" } } } });
        _factory.PoolMock
            .Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["MATERIALDOCUMENT"] = "5000009999" } });

        var client = _factory.CreateAuthenticatedClient(userId: 1);
        var response = await client.PostAsJsonAsync("/api/production/mixing-scrap", new MixingScrapRequest
        {
            Material = "30005R", Quantity = 25.5m, Header = "MX00000123", ScrapReason = "4917", TestRun = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.PoolMock.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_ROLLBACK"), It.IsAny<CancellationToken>()), Times.Once);
        _factory.PoolMock.Verify(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(),
            It.Is<RfcRequest>(r => r.FunctionName == "BAPI_TRANSACTION_COMMIT"), It.IsAny<CancellationToken>()), Times.Never);
    }
}
