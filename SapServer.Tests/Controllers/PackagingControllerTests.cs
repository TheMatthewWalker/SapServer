using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapServer.Configuration;
using SapServer.Controllers;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class PackagingControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly PackagingController _controller;

    private static RfcResponse EmptyDisplay() => new();
    private static RfcResponse SingleRow(string wa) => new()
    {
        Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = wa } } },
    };
    private static RfcResponse Bdc(string type, string message) => new()
    {
        Parameters = new() { ["MESSG"] = $"{type}    ZZ   001 {message}" },
    };

    public PackagingControllerTests()
    {
        var poolOptions = Options.Create(new SapPoolOptions
        {
            ServiceAccount = new SapConnectionOptions { System = "SAP", Client = "100", SystemId = "01", Language = "EN" },
        });
        _controller = new PackagingController(_pool.Object, _permissions.Object, poolOptions, NullLogger<PackagingController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task MaterialExists_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, PackagingHelpers.FnCreate, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.MaterialExists("30005R", CancellationToken.None));
    }

    [Fact]
    public async Task MaterialDetails_400s_when_the_material_is_not_found_in_MARA()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyDisplay());
        var result = await _controller.MaterialDetails("30005R", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task MaterialDetails_returns_the_parsed_row_converting_grams_to_kg()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(SingleRow("5000|ROH|X|KG"));
        var result = await _controller.MaterialDetails("30005R", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<PackagingMaraRow>>(ok.Value);
        Assert.Equal(5.0m, body.Data!.WeightKg);
    }

    [Fact]
    public async Task GetInstruction_404s_when_none_exists()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyDisplay());
        var result = await _controller.GetInstruction("30005R", null, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SaveInstruction_422s_when_SAP_reports_a_non_zero_return_code()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["RC"] = "4" } });

        var result = await _controller.SaveInstruction(new PackagingInstrSaveRequest { Material = "30005R" }, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task SaveInstruction_succeeds_on_return_code_zero()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["RC"] = "0" } });

        var result = await _controller.SaveInstruction(new PackagingInstrSaveRequest { Material = "30005R" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task MassUpdate_skips_a_material_with_no_existing_plant_default_row_without_calling_update()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyDisplay());

        var result = await _controller.MassUpdate(new MassPackagingUpdateRequest
        {
            Rows = [new MassPackagingUpdateRow { Material = "30005R", PackMaterial = "IB_363800_SD" }],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<MassPackagingUpdateResult>>>(ok.Value);
        Assert.False(body.Data![0].Success);
        Assert.Contains("use Packaging Instruction Detail", body.Data[0].Message);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the existence check
    }

    [Fact]
    public async Task MassUpdate_updates_a_material_with_an_existing_row()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleRow("1000|500|X|||||X|X|X")) // existing plant-default row (PalletQty, SmallBoxQty, PackProd, ...) — needs >= 10 columns
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["RC"] = "0" } }); // update result

        var result = await _controller.MassUpdate(new MassPackagingUpdateRequest
        {
            Rows = [new MassPackagingUpdateRow { Material = "30005R", PackMaterial = "IB_363800_SD" }],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<MassPackagingUpdateResult>>>(ok.Value);
        Assert.True(body.Data![0].Success);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Create_reports_an_unknown_packaging_code_without_calling_the_pool()
    {
        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["ZZ"] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok.Value);
        Assert.Contains("Unknown packaging code", body.Data![0].Message);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_skips_a_code_whose_material_already_exists()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(SingleRow("3012"));

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok.Value);
        Assert.True(body.Data![0].AlreadyExisted);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the exists check
    }

    [Fact]
    public async Task Create_stops_before_CS01_when_MM01_fails()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay())                          // exists check: does not exist
            .ReturnsAsync(Bdc("E", "Material type invalid"));       // MM01: fails

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok.Value);
        Assert.False(body.Data![0].MaterialCreated);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // never reaches CS01
    }

    [Fact]
    public async Task Create_creates_both_the_material_and_the_BOM_on_full_success()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay())                     // exists check: does not exist
            .ReturnsAsync(Bdc("S", "Material created"))       // MM01: success
            .ReturnsAsync(Bdc("S", "BOM created"));           // CS01: success

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok.Value);
        Assert.True(body.Data![0].MaterialCreated);
        Assert.True(body.Data[0].BomCreated);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task CreateElevated_requires_credentials_before_acquiring_a_worker()
    {
        var result = await _controller.CreateElevated(new CreatePackagingElevatedRequest { CustomerPart = "CUST123" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _pool.Verify(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateElevated_always_releases_the_elevated_worker_on_success()
    {
        _pool.Setup(p => p.AcquireElevatedWorkerAsync(It.IsAny<SapConnectionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SapWorkerHandle)null!);
        _pool.Setup(p => p.ExecuteOnWorkerAsync(It.IsAny<SapWorkerHandle>(), It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay()); // treats every code as "does not exist yet" then... actually just needs to not throw

        var result = await _controller.CreateElevated(new CreatePackagingElevatedRequest
        {
            SapUsername = "j.smith", SapPassword = "pw", CustomerPart = "CUST123", Codes = ["SD"],
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _pool.Verify(p => p.ReleaseElevatedWorkerAsync(It.IsAny<SapWorkerHandle>()), Times.Once);
    }
}
