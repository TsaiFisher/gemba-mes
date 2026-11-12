using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.ACT.Controllers;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 會計期間 controller 的結案／重開。狀態轉換本身在 <see cref="AccountingPeriodServiceTests"/>；
/// 這裡守的是 controller 這一層自己的三件事：
///   ① 沒有更新權限不得呼叫 service；
///   ② 廠別與使用者一定取自目前登入環境——期間主鍵是 (PeriodId, PlantId)，
///      傳錯廠別等於關到／重開到別廠的帳，而且不會報錯；
///   ③ service 擋下來的理由（未關帳前置未完成、期間不存在…）要原封回到畫面。
/// </summary>
public class AccountingPeriodControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private const string Period  = "202609";

    private readonly Mock<IPermissionService>      _perm    = new();
    private readonly Mock<IUserContext>            _user    = new();
    private readonly Mock<IAccountingPeriodService> _periods = new();

    private AccountingPeriodController BuildController(bool canUpdate = true)
    {
        _perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(canUpdate);
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns(UserId);
        return new AccountingPeriodController(
            _context, _perm.Object, _user.Object, _periods.Object,
            new Mock<IMonthEndService>().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
    }

    private static (bool success, string message) Read(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        var t = json.Value!.GetType();
        return ((bool)t.GetProperty("success")!.GetValue(json.Value)!,
                (string)t.GetProperty("message")!.GetValue(json.Value)!);
    }

    // ── Close ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Close_CallsServiceWithCurrentPlantAndUser()
    {
        var (success, _) = Read(await BuildController().Close(Period));

        Assert.True(success);
        _periods.Verify(s => s.ClosePeriodAsync(Period, PlantId, UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Close_NoUpdatePermission_DoesNotCallService()
    {
        var (success, _) = Read(await BuildController(canUpdate: false).Close(Period));

        Assert.False(success);
        _periods.Verify(s => s.ClosePeriodAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_ServiceRejects_ReturnsItsMessage()
    {
        _periods.Setup(s => s.ClosePeriodAsync(Period, PlantId, UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("尚有未過帳單據"));

        var (success, message) = Read(await BuildController().Close(Period));

        Assert.False(success);
        Assert.Equal("尚有未過帳單據", message);
    }

    // ── Reopen ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reopen_CallsServiceWithCurrentPlantAndUser()
    {
        var (success, _) = Read(await BuildController().Reopen(Period));

        Assert.True(success);
        _periods.Verify(s => s.ReopenPeriodAsync(Period, PlantId, UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reopen_NoUpdatePermission_DoesNotCallService()
    {
        var (success, _) = Read(await BuildController(canUpdate: false).Reopen(Period));

        Assert.False(success);
        _periods.Verify(s => s.ReopenPeriodAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reopen_ServiceRejects_ReturnsItsMessage()
    {
        _periods.Setup(s => s.ReopenPeriodAsync(Period, PlantId, UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("僅已關帳期間可重開"));

        var (success, message) = Read(await BuildController().Reopen(Period));

        Assert.False(success);
        Assert.Equal("僅已關帳期間可重開", message);
    }
}
