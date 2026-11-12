using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Controllers;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPS.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Timezone;

/// <summary>
/// 報表工具三頁的日期篩選日界（manual-test-plan §10.3 <b>TZ-4</b>／§10.2 <b>R-12</b>）。
///
/// <para>§11 #11 在 2026-09-11 把顯示與篩選都改成跟隨廠別時區，但<b>沒有留下自動化</b>
/// （TZ-4 覆蓋一欄一直是 ❌）。這一組補的就是那個缺口。</para>
///
/// <para><b>為什麼篩選那一半才是重點</b>：顯示錯看得出來——時間就寫在畫面上；
/// 篩選錯只是少幾列，沒有錯誤訊息、沒有紅字，使用者只會覺得「報表還沒好」。
/// 症狀是 UTC+8 廠的人在當地上午 8 點前用「今天」查，看不到一小時前才產出的報表。</para>
///
/// <para><b>為什麼每個案例都要配一支「查不到」</b>：只斷言「查得到」的話，
/// 把整段篩選條件刪掉也會綠——那正是最容易發生的回歸。</para>
/// </summary>
public class ReportFilterTimezoneTests : TestBase
{
    private const string UserId = "User1";
    private const string Taipei = TimezoneTestKit.Taipei;   // UTC+8，無日光節約

    /// <summary>使用者當地的那一天。</summary>
    private static readonly DateTime LocalDay = new(2026, 3, 10);

    /// <summary>當地 03/10 07:00 ＝ UTC 03/09 23:00。UTC 日界會把它算成「前一天」。</summary>
    private static readonly DateTime EarlyMorningRunUtc = new(2026, 3, 9, 23, 0, 0, DateTimeKind.Utc);

    /// <summary>當地 03/09 23:00 ＝ UTC 03/09 15:00。確實屬於前一天，不該被撈進來。</summary>
    private static readonly DateTime PreviousDayRunUtc = new(2026, 3, 9, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// UTC 03/11 05:00。三種日界各自的 03/10 區間裡，<b>只有 UTC−11 的涵蓋它</b>：
    /// 台北是 UTC 03/09 16:00–03/10 16:00、UTC 午夜是 03/10 00:00–03/11 00:00，兩者都不含；
    /// UTC−11 是 03/10 11:00–03/11 11:00，含。挑這個時刻才分得出「日界真的跟著時區走」
    /// 與「日界寫死在 UTC」——換成別的時刻，兩種錯誤實作都會給出同樣的空結果。
    /// </summary>
    private static readonly DateTime FarWestOnlyRunUtc = new(2026, 3, 11, 5, 0, 0, DateTimeKind.Utc);

    private const long EarlyMorningRunId = 1001;
    private const long PreviousDayRunId  = 1002;
    private const long FarWestOnlyRunId  = 1003;

    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IUserContext> _user = new();

    public ReportFilterTimezoneTests()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _user.Setup(u => u.UserId).Returns(UserId);
        _user.Setup(u => u.TimeZoneId).Returns(Taipei);

        _context.ReportDefinitions.Add(new ReportDefinition
        {
            Id = 1, Name = "測試報表", ToolKey = "demo", OwnerUserId = UserId,
        });
        _context.ReportRuns.AddRange(
            NewRun(EarlyMorningRunId, EarlyMorningRunUtc),
            NewRun(PreviousDayRunId,  PreviousDayRunUtc),
            NewRun(FarWestOnlyRunId,  FarWestOnlyRunUtc));
        _context.SaveChanges();
    }

    private static ReportRun NewRun(long runId, DateTime runAtUtc) => new()
    {
        RunId = runId, DefinitionId = 1, RunAt = runAtUtc,
        Status = ReportRunStatus.Success, TriggerType = "Manual",
        FilePath = $"/reports/{runId}.xlsx", CreatedBy = UserId,
    };

    // 換算走**真實**的 DateTimeService：mock 掉就等於沒測到時區邏輯（見 TimezoneTestKit 的說明）。
    private IDateTimeService RealTaipei => TimezoneTestKit.RealIn(Taipei);

    private ReportRunHistoryController BuildHistory()
    {
        var c = new ReportRunHistoryController(_context, _perm.Object, RealTaipei);
        c.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _perm.Object, userContext: _user.Object,
            dateTimeService: RealTaipei);
        return c;
    }

    private ReportInboxController BuildInbox()
    {
        var factory = new Mock<IStringLocalizerFactory>();
        factory.Setup(f => f.Create(It.IsAny<Type>()))
               .Returns(GetMockLocalizer<ProjectZSharedResource>().Object);

        var c = new ReportInboxController(
            _context, _perm.Object,
            new Mock<IReportSchedulePermissionChecker>().Object,
            new Mock<IReportFileStore>().Object,
            factory.Object, RealTaipei);
        c.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _perm.Object, userContext: _user.Object,
            dateTimeService: RealTaipei);
        return c;
    }

    private static List<long> RunIdsOf<T>(IActionResult result, Func<T, long> id)
    {
        var list = Assert.IsType<ViewResult>(result).Model as PaginatedList<T>;
        Assert.NotNull(list);
        return list!.Select(id).ToList();
    }

    // ── 執行紀錄 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunHistory_LocalTodayFilter_FindsTheRunFromThisLocalMorning()
    {
        // 當地 03/10 07:00 產出的報表，用當地的「03/10」查必須找得到。
        // 日界若取 UTC 午夜，這一筆（UTC 03/09 23:00）會落在查詢區間之前。
        var result = await BuildHistory().Index(
            status: null, fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        Assert.Contains(EarlyMorningRunId,
            RunIdsOf<ReportRunHistoryItemVm>(result, r => r.RunId));
    }

    [Fact]
    public async Task RunHistory_LocalTodayFilter_ExcludesTheRunFromThePreviousLocalDay()
    {
        // 配對案例：沒有它，「把篩選整段刪掉」也會讓上面那支變綠。
        var result = await BuildHistory().Index(
            status: null, fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        Assert.DoesNotContain(PreviousDayRunId,
            RunIdsOf<ReportRunHistoryItemVm>(result, r => r.RunId));
    }

    // ── 收件匣 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inbox_LocalTodayFilter_FindsTheRunFromThisLocalMorning()
    {
        var result = await BuildInbox().Index(
            fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        Assert.Contains(EarlyMorningRunId,
            RunIdsOf<ReportInboxItemVm>(result, r => r.RunId));
    }

    [Fact]
    public async Task Inbox_LocalTodayFilter_ExcludesTheRunFromThePreviousLocalDay()
    {
        var result = await BuildInbox().Index(
            fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        Assert.DoesNotContain(PreviousDayRunId,
            RunIdsOf<ReportInboxItemVm>(result, r => r.RunId));
    }

    // ── 換一個時區，日界真的要跟著換 ────────────────────────────────────────

    [Fact]
    public async Task RunHistory_TheSameQueryInAnotherZone_ResolvesToADifferentDay()
    {
        // 同一組輸入（03/10）在 UTC−11 應該撈到**完全不同的一筆**：
        // 台北早上那筆落在它的 03/09，而 FarWestOnly 那筆才是它的 03/10。
        // 日界若寫死成 UTC 午夜，兩邊結果會一樣，這一支就會紅。
        var farWest = TimezoneTestKit.RealIn(TimezoneTestKit.FarWest);
        var c = new ReportRunHistoryController(_context, _perm.Object, farWest);
        c.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _perm.Object, userContext: _user.Object,
            dateTimeService: farWest);

        var result = await c.Index(
            status: null, fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        var ids = RunIdsOf<ReportRunHistoryItemVm>(result, r => r.RunId);
        Assert.Contains(FarWestOnlyRunId, ids);
        Assert.DoesNotContain(EarlyMorningRunId, ids);
    }

    [Fact]
    public async Task RunHistory_LocalTodayFilter_ExcludesWhatBelongsToAnotherZonesToday()
    {
        // 反向：FarWestOnly 那筆對台北而言是 03/11 凌晨，不該出現在 03/10 的查詢裡。
        var result = await BuildHistory().Index(
            status: null, fromDate: LocalDay, toDate: LocalDay,
            pageNumber: null, pageSize: null, sortOrder: null, searched: true);

        Assert.DoesNotContain(FarWestOnlyRunId,
            RunIdsOf<ReportRunHistoryItemVm>(result, r => r.RunId));
    }
}
