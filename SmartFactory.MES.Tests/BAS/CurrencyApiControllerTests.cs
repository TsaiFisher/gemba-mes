using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

public class CurrencyApiControllerTests
{
    private readonly Mock<ICurrencyService> _mock = new();
    private readonly Mock<IPermissionService> _perm = new();

    public CurrencyApiControllerTests()
    {
        // 匯率查詢自 2026-08-03 起有讀取閘門（R24）；預設給通過，另有專測驗擋人。
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
    }

    private CurrencyApiController CreateController() => new(_mock.Object, _perm.Object);

    [Fact]
    public async Task GetRate_NoReadPermissionOnAnyCaller_ReturnsForbid()
    {
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);
        var ctrl = new CurrencyApiController(_mock.Object, perm.Object);

        var result = await ctrl.GetRate("USD", new DateOnly(2026, 5, 20), RateType.Buy);

        Assert.IsType<ForbidResult>(result);
        _mock.Verify(s => s.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRate_BlankCurrencyCode_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.GetRate("", new DateOnly(2026, 5, 20), RateType.Buy);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRate_Found_ReturnsSnapshot()
    {
        var docDate = new DateOnly(2026, 5, 20);
        var rateDate = new DateOnly(2026, 5, 18);
        _mock.Setup(s => s.GetSnapshotAsync("USD", docDate, RateType.Buy))
             .ReturnsAsync(new CurrencyRateSnapshot("USD", rateDate, 31.5m, RateType.Buy));

        var ctrl = CreateController();
        var result = await ctrl.GetRate("USD", docDate, RateType.Buy);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var props = ok.Value!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(ok.Value));
        Assert.Equal("USD", props["currencyCode"]);
        Assert.Equal(31.5m, props["rate"]);
        Assert.Equal(rateDate, props["rateDate"]);
        Assert.Equal(true, props["found"]);
    }

    [Fact]
    public async Task GetRate_NotFound_ReturnsNullRateWithFoundFalse()
    {
        var docDate = new DateOnly(2026, 5, 20);
        _mock.Setup(s => s.GetSnapshotAsync("ZWL", docDate, RateType.Buy))
             .ReturnsAsync((CurrencyRateSnapshot?)null);

        var ctrl = CreateController();
        var result = await ctrl.GetRate("ZWL", docDate, RateType.Buy);

        var ok = Assert.IsType<OkObjectResult>(result);
        var props = ok.Value!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(ok.Value));
        Assert.Null(props["rate"]);
        Assert.Equal(false, props["found"]);
    }

    [Fact]
    public async Task GetAllDecimals_DelegatesToService()
    {
        var map = new Dictionary<string, int> { ["TWD"] = 0, ["USD"] = 2, ["JPY"] = 0 };
        _mock.Setup(s => s.GetAllDecimalPlacesAsync(default)).ReturnsAsync(map);

        var ctrl = CreateController();
        var result = await ctrl.GetAllDecimals();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(map, ok.Value);
    }

    [Theory]
    [InlineData(RateType.Buy)]
    [InlineData(RateType.Sell)]
    [InlineData(RateType.Mid)]
    public async Task GetRate_PassesRateTypeToService(RateType rateType)
    {
        var docDate = new DateOnly(2026, 5, 20);
        _mock.Setup(s => s.GetSnapshotAsync("EUR", docDate, rateType))
             .ReturnsAsync(new CurrencyRateSnapshot("EUR", docDate, 33m, rateType));

        var ctrl = CreateController();
        await ctrl.GetRate("EUR", docDate, rateType);

        _mock.Verify(s => s.GetSnapshotAsync("EUR", docDate, rateType), Times.Once);
    }
}
