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
using System.Web.Http;

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
        var poolOptions = Options.Create(new SapNcoOptions
        {
            ServiceAccount = new SapConnectionOptions { AppServerHost = "sap-test-host", Client = "100", SystemNumber = "01", Language = "EN" },
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
        ControllerTestHelpers.AssertBadRequest(result);
    }

    [Fact]
    public async Task MaterialDetails_returns_the_parsed_row_with_no_extra_scaling_on_the_weight()
    {
        // "5,000" is SAP's native comma-decimal format (no thousands
        // grouping) for the plain value 5 -- see PackagingHelpersTests'
        // ParseMara regression test for why this isn't divided by 1000.
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(SingleRow("5,000|ROH|X|KG"));
        var result = await _controller.MaterialDetails("30005R", CancellationToken.None);
        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<PackagingMaraRow>>(ok);
        Assert.Equal(5.0m, body.Data!.WeightKg);
    }

    [Fact]
    public async Task GetInstruction_404s_when_none_exists()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyDisplay());
        var result = await _controller.GetInstruction("30005R", null, CancellationToken.None);
        ControllerTestHelpers.AssertNotFound(result);
    }

    [Fact]
    public async Task SaveInstruction_422s_when_SAP_reports_a_non_zero_return_code()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["RC"] = "4" } });

        var result = await _controller.SaveInstruction(new PackagingInstrSaveRequest { Material = "30005R" }, CancellationToken.None);

        ControllerTestHelpers.AssertUnprocessableEntity(result);
    }

    [Fact]
    public async Task SaveInstruction_succeeds_on_return_code_zero()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfcResponse { Parameters = new() { ["RC"] = "0" } });

        var result = await _controller.SaveInstruction(new PackagingInstrSaveRequest { Material = "30005R" }, CancellationToken.None);

        ControllerTestHelpers.AssertOk(result);
    }

    [Fact]
    public async Task MassUpdate_skips_a_material_with_no_existing_plant_default_row_without_calling_update()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyDisplay());

        var result = await _controller.MassUpdate(new MassPackagingUpdateRequest
        {
            Rows = [new MassPackagingUpdateRow { Material = "30005R", PackMaterial = "IB_363800_SD" }],
        }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<MassPackagingUpdateResult>>>(ok);
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

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<MassPackagingUpdateResult>>>(ok);
        Assert.True(body.Data![0].Success);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Create_reports_an_unknown_packaging_code_without_calling_the_pool()
    {
        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["ZZ"] }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok);
        Assert.Contains("Unknown packaging code", body.Data![0].Message);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_skips_a_code_whose_material_already_exists()
    {
        _pool.Setup(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(SingleRow("3012"));

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok);
        Assert.True(body.Data![0].AlreadyExisted);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Once); // only the exists check
    }

    [Fact]
    public async Task Create_stops_before_CS01_when_MM01_fails()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay())                          // exists check: does not exist
            .ReturnsAsync(SingleRow("M"))                          // reference material's industry sector
            .ReturnsAsync(Bdc("E", "Material type invalid"));       // MM01: fails

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok);
        Assert.False(body.Data![0].MaterialCreated);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3)); // never reaches CS01
    }

    [Fact]
    public async Task Create_fails_cleanly_when_the_reference_materials_industry_sector_cannot_be_read()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay())   // exists check: does not exist
            .ReturnsAsync(EmptyDisplay());  // industry sector read: nothing found

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok);
        Assert.False(body.Data![0].MaterialCreated);
        Assert.Contains("industry sector", body.Data[0].Message);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // never reaches MM01
    }

    [Fact]
    public async Task Create_creates_both_the_material_and_the_BOM_on_full_success()
    {
        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyDisplay())                     // exists check: does not exist
            .ReturnsAsync(SingleRow("M"))                     // reference material's industry sector
            .ReturnsAsync(Bdc("S", "Material created"))       // MM01: success
            .ReturnsAsync(Bdc("S", "BOM created"));           // CS01: success

        var result = await _controller.Create(new CreatePackagingRequest { CustomerPart = "CUST123", Codes = ["SD"] }, CancellationToken.None);

        var ok = ControllerTestHelpers.AssertOk(result);
        var body = Assert.IsType<ApiResponse<List<CreatePackagingResult>>>(ok);
        Assert.True(body.Data![0].MaterialCreated);
        Assert.True(body.Data[0].BomCreated);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task CreateElevated_requires_credentials_before_acquiring_a_worker()
    {
        var result = await _controller.CreateElevated(new CreatePackagingElevatedRequest { CustomerPart = "CUST123" }, CancellationToken.None);

        ControllerTestHelpers.AssertBadRequest(result);
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

        ControllerTestHelpers.AssertOk(result);
        _pool.Verify(p => p.ReleaseElevatedWorkerAsync(It.IsAny<SapWorkerHandle>()), Times.Once);
    }
}
