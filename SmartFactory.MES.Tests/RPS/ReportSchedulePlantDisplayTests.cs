using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Controllers;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPS.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 報表排程畫面的「對象廠別」（2026-10-07 人工驗收）。
///
/// <para>排程建在哪個廠別，報表就只查那個廠別的資料、期間與執行時刻依它的時區；
/// 但清單與表單原本都看不到廠別。驗收時排程建在 P2、資料在 ADM，得到「成功、0 筆」，
/// 畫面上沒有任何線索。本畫面是管理視角（看得到全部廠別），不同廠別的排程又混在同一張清單。</para>
/// </summary>
public class ReportSchedulePlantDisplayTests : TestBase
{
    private const string Here  = "ADM";
    private const string Other = "P2";

    public ReportSchedulePlantDisplayTests()
    {
        _context.Plants.AddRange(
            new Plant { Id = Here,  Name = "總公司", NameEn = "Head Office", DefaultTimeZone = "Asia/Taipei" },
            new Plant { Id = Other, Name = "二廠",   NameEn = "Plant 2",     DefaultTimeZone = "America/New_York" });
        _context.ReportDefinitions.AddRange(
            Schedule(1, "here-1", Here), Schedule(2, "here-2", Here), Schedule(3, "other-1", Other));
        _context.SaveChanges();
    }

    private static ReportDefinition Schedule(int id, string name, string plantId) => new()
    {
        Id = id, Name = name, PlantId = plantId, OwnerUserId = "User1", OwnerRoleId = "1",
        ToolKey = "SalesRanking", ParamsJson = "{}", ScheduleKind = ReportScheduleKind.Daily,
        ScheduleHour = 8, ScheduleMinute = 0, OutputFormat = "Excel", CreatedByRoleId = "1",
        NextRunAt = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
    };

    private ReportScheduleController New()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(Here);
        user.Setup(u => u.UserId).Returns("User1");

        var catalog = new Mock<IReportToolCatalog>();
        catalog.Setup(c => c.GetAllWithAccessAsync()).ReturnsAsync(Array.Empty<ReportToolAccess>());

        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        var factory = new Mock<IStringLocalizerFactory>();
        factory.Setup(f => f.Create(It.IsAny<Type>())).Returns(new Mock<IStringLocalizer>().Object);

        return new ReportScheduleController(_context, perm.Object, catalog.Object, factory.Object)
        {
            ControllerContext = GetControllerContextWithServices(permService: perm.Object, userContext: user.Object),
        };
    }

    private static List<string> NamesOf(IActionResult r)
        => ((PaginatedList<ReportDefinition>)Assert.IsType<ViewResult>(r).Model!).Select(d => d.Name).OrderBy(n => n).ToList();

    [Fact]
    public async Task 清單預設只列目前廠別的排程()
    {
        var r = await New().Index(null, null, null, plantFilter: null, null, null, null, searched: true);

        Assert.Equal(["here-1", "here-2"], NamesOf(r));
        Assert.Equal(Here, ((ViewResult)r).ViewData["PlantFilter"]);
    }

    [Fact]
    public async Task 選全部廠別時列出所有廠別的排程()
    {
        var r = await New().Index(null, null, null, ReportScheduleController.AllPlants, null, null, null, searched: true);

        Assert.Equal(["here-1", "here-2", "other-1"], NamesOf(r));
    }

    [Fact]
    public async Task 選特定廠別時只列該廠別()
    {
        var r = await New().Index(null, null, null, Other, null, null, null, searched: true);

        Assert.Equal(["other-1"], NamesOf(r));
    }

    /// <summary>下次執行時刻要依**排程所屬時區**顯示：P2 每日 08:00 的排程在 ADM 看仍是 08:00。</summary>
    [Fact]
    public async Task 清單提供每筆排程所屬時區_下次執行依該時區顯示()
    {
        var r = (ViewResult)await New().Index(null, null, null, ReportScheduleController.AllPlants, null, null, null, searched: true);
        var zones = Assert.IsAssignableFrom<IReadOnlyDictionary<int, string?>>(r.ViewData["TimeZones"]);

        Assert.Equal("America/New_York", zones[3]);
        Assert.Equal("Asia/Taipei", zones[1]);
        // 2026-10-07 12:00 UTC＝紐約 08:00（夏令 UTC−4）
        Assert.Equal(new DateTime(2026, 10, 7, 8, 0, 0),
            ReportPlantDisplay.ToScheduleLocal(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), zones[3]));
    }

    [Fact]
    public async Task 新增表單顯示目前廠別為對象廠別()
    {
        var vm = Assert.IsType<ReportScheduleFormVm>(Assert.IsAssignableFrom<ViewResult>(await New().Create()).Model);

        Assert.StartsWith(Here, vm.PlantLabel);
        Assert.Equal("Asia/Taipei", vm.TimeZoneId);
    }

    /// <summary>編輯顯示的是排程**原本**的廠別，不是看的人目前所在的廠別。</summary>
    [Fact]
    public async Task 編輯表單顯示排程原本的廠別()
    {
        var vm = Assert.IsType<ReportScheduleFormVm>(Assert.IsAssignableFrom<ViewResult>(await New().Edit(3)).Model);

        Assert.StartsWith(Other, vm.PlantLabel);
        Assert.Equal("America/New_York", vm.TimeZoneId);
    }
}
