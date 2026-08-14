using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapServer.Controllers;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Services.Interfaces;
using SapServer.Tests.Infrastructure;

namespace SapServer.Tests.Controllers;

public class SalesControllerTests
{
    private readonly Mock<ISapConnectionPool> _pool = new();
    private readonly Mock<IPermissionService> _permissions = new();
    private readonly SalesController _controller;

    private static ScheduleWaterfallRequest BaseRequest() => new()
    {
        SalesOrg = "302",
        ShipToParties = ["302879"],
        ScheduleDateFrom = new DateOnly(2020, 1, 1),
        ScheduleDateTo = new DateOnly(2020, 12, 31),
    };

    public SalesControllerTests()
    {
        _controller = new SalesController(_pool.Object, _permissions.Object, NullLogger<SalesController>.Instance);
        ControllerTestHelpers.SetUser(_controller, userId: 1);
        _permissions.Setup(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task ScheduleWaterfall_short_circuits_on_missing_sales_org_or_ship_to_without_checking_permission()
    {
        var result = await _controller.ScheduleWaterfall(BaseRequest() with { SalesOrg = "" }, CancellationToken.None);

        Assert.Empty(Assert.IsType<ApiResponse<ScheduleWaterfallRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _permissions.Verify(p => p.CanExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleWaterfall_throws_permission_exception_when_denied()
    {
        _permissions.Setup(p => p.CanExecuteAsync(1, SalesHelpers.FnReadTables, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<SapPermissionException>(() => _controller.ScheduleWaterfall(BaseRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleWaterfall_queries_both_history_and_current_and_unions_the_rows()
    {
        var history = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "0000302879|30103645|000010|31446702|DESC|25753188|150|20200102|080000|20200106|540|0302|1|3" },
                }
            }
        };
        var current = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "0000302879|30103645|000010|31446702|DESC|00000000|0|00000000|000000|20200113|494|0302|1|3" },
                }
            }
        };

        _pool.SetupSequence(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(history)
            .ReturnsAsync(current);

        var result = await _controller.ScheduleWaterfall(BaseRequest(), CancellationToken.None);

        var rows = Assert.IsType<ApiResponse<ScheduleWaterfallRow[]>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => !r.IsCurrent && r.IdocNumber == "25753188");
        Assert.Contains(rows, r => r.IsCurrent);
        _pool.Verify(p => p.ExecuteAsync(It.IsAny<RfcRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
