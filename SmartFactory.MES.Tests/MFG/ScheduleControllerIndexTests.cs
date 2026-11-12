using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 排程畫面預設迄日＝廠別當地今天＋14 天。
/// <para>2026-10-19 前把廠別代號（plantId）當時區代碼傳給 GetLocalDate：一律解不開、退回台北並寫警告
/// （UI 全套跑出 13 筆），紐約廠的預設迄日因此可能差一天。這組測試釘住「傳的是廠別主檔的時區」。</para>
/// </summary>
public class ScheduleControllerIndexTests : TestBase
{
    private const string NyTz = "America/New_York";
    // 三個不同的日期：哪一個被用到，就知道 GetLocalDate 拿到的是什麼
    private static readonly DateOnly NyToday       = new(2026, 10, 20);
    private static readonly DateOnly UserTzToday   = new(2026, 10, 21);
    private static readonly DateOnly WrongArgToday = new(2000, 1, 1);

    private readonly Mock<IDateTimeService> _dt = new();
    private readonly Mock<IUserContext> _user = new();

    public ScheduleControllerIndexTests()
    {
        _context.Plants.Add(new Plant { Id = "NY", Name = "紐約廠", DefaultTimeZone = NyTz });
        _context.Plants.Add(new Plant { Id = "NOTZ", Name = "未設時區廠", DefaultTimeZone = null });
        _context.SaveChanges();

        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(WrongArgToday);
        _dt.Setup(d => d.GetLocalDate(NyTz)).Returns(NyToday);
        _dt.Setup(d => d.GetLocalDate(null)).Returns(UserTzToday);   // null＝使用者時區（DateTimeService 的退回順序）
        _user.Setup(u => u.UserId).Returns("User1");
    }

    private ScheduleController Build(string userPlant)
    {
        _user.Setup(u => u.PlantId).Returns(userPlant);
        var ctrl = new ScheduleController(new Mock<ISchedulingEngine>().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object, _dt.Object);
        // IAutoScheduleService 不註冊：EnsureScheduleFreshAsync 依設計直接略過（見 AutoScheduleHttpExtensions）
        ctrl.ControllerContext = GetControllerContextWithServices(
            userContext: _user.Object, dateTimeService: _dt.Object,
            configureServices: s => s.AddSingleton(new Mock<IPlantParamService>().Object));
        return ctrl;
    }

    private static string? DateTo(IActionResult r) => Assert.IsType<ViewResult>(r).ViewData["DateTo"] as string;

    [Fact]
    public async Task Index_DefaultDateTo_UsesPlantTimeZone_NotPlantCode()
    {
        var r = await Build(userPlant: "NY").Index(plantId: null, dateTo: null);

        Assert.Equal(NyToday.AddDays(14).ToString("yyyy-MM-dd"), DateTo(r));
        _dt.Verify(d => d.GetLocalDate("NY"), Times.Never);   // 原本的錯：廠別代號當時區
    }

    [Fact]
    public async Task Index_OtherPlantInUrl_UsesThatPlantsTimeZone()
    {
        // 使用者在 NOTZ 廠、網址帶 NY：迄日要用 NY 的當地日期，不是使用者所在廠
        var r = await Build(userPlant: "NOTZ").Index(plantId: "NY", dateTo: null);

        Assert.Equal(NyToday.AddDays(14).ToString("yyyy-MM-dd"), DateTo(r));
    }

    [Fact]
    public async Task Index_PlantWithoutTimeZone_FallsBackToUserTimeZone()
    {
        var r = await Build(userPlant: "NOTZ").Index(plantId: null, dateTo: null);

        Assert.Equal(UserTzToday.AddDays(14).ToString("yyyy-MM-dd"), DateTo(r));
        _dt.Verify(d => d.GetLocalDate("NOTZ"), Times.Never);
    }

    [Fact]
    public async Task Index_ExplicitDateTo_IsKept()
    {
        var r = await Build(userPlant: "NY").Index(plantId: null, dateTo: "2026-12-31");

        Assert.Equal("2026-12-31", DateTo(r));
    }
}
