using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Timezone;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 排程執行時，「昨天」是**誰的**昨天（人工驗收 §10.3 TZ-3）。
///
/// <para><b>這一組防的是什麼</b>：2026-09-15 查證 TZ-3 時發現它不只是缺測試，是**真缺陷**。
/// <c>ReportRequestBase.TimeZoneId</c> 的註解寫著「排程沒有 HTTP 上下文，不傳會退回行程預設，
/// 跨時區廠別的『今天』會差一天」——那句話是對的，只是**沒有人傳**：
/// 全庫沒有任何一行寫入這個屬性，而八支工具解算相對期間時一律
/// <c>ResolveAsync(request, ct: ct)</c>（時區參數留空）。於是這個屬性在相對期間這條路上
/// 是個**沒有寫入點、也沒有讀取點的欄位**，排程的「昨天」永遠是 Asia/Taipei 的昨天。</para>
///
/// <para>🚨 <b>為什麼派工器 13 支全綠也擋不住</b>：派工器測的是「有沒有執行、失敗有沒有推進、
/// 收件人語系有沒有去重」，一條都不碰查詢區間；而 <c>RelativePeriodResolverTests</c> 22 支
/// 餵的是固定日期的 mock，<b>mock 回什麼就是什麼，時區參數有沒有傳根本不影響結果</b>。
/// 兩邊各自厚實，缺陷正好落在兩邊中間那條線上。</para>
///
/// <para><b>症狀的形狀</b>：紐約廠（UTC−5）的每日排程在當地 08:00 執行，
/// 台北時間是同日 21:00——兩邊的「昨天」是同一天，所以**大部分日子看起來完全正常**。
/// 只有當排程落在兩地日界之間時才差一天，而差的那一天沒有任何訊號：
/// 報表格式正確、筆數合理，只是少了（或多了）一天的單。</para>
///
/// <para>三層各有一組，對應修正的三個落點——任何一層被改回去都會紅，
/// 而且訊息指得出是哪一層。</para>
/// </summary>
public class ReportScheduleTimeZoneFlowTests : TestBase
{
    /// <summary>
    /// ReportToolRunner 從這裡讀 CurrentPlantId 去補 request.PlantId（契約：null ＝ 依執行 scope）。
    /// 本測試不驗廠別，空的 InMemory 上下文即可——但**不能不給**：
    /// 讓它可為 null 的話，真正的呼叫端漏掉時同樣不會有聲音。
    /// </summary>
    private static ApplicationDbContext NewRunnerDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>UTC+14 與 UTC−11 相距 25 小時，任何時刻當地日期必然不同（見 TimezoneTestKit）。</summary>
    private const string FarEast = TimezoneTestKit.FarEast;
    private const string FarWest = TimezoneTestKit.FarWest;

    private static readonly DateOnly EastToday = new(2026, 5, 15);
    private static readonly DateOnly WestToday = new(2026, 5, 14);
    private static readonly DateOnly FallbackToday = new(2026, 1, 1);

    /// <summary>
    /// 三個時區各回一個**互不相同**的「今天」。斷言因此看得出解算時到底用了哪一個，
    /// 而不是只看出「有日期」——固定單一值的 mock 正是原缺陷躲過 22 支測試的原因。
    /// </summary>
    private readonly Mock<IDateTimeService> _dt = new();

    public ReportScheduleTimeZoneFlowTests()
    {
        _context.CurrentPlantId = "P001";
        _dt.Setup(d => d.GetLocalDate(FarEast)).Returns(EastToday);
        _dt.Setup(d => d.GetLocalDate(FarWest)).Returns(WestToday);
        _dt.Setup(d => d.GetLocalDate(It.Is<string?>(tz => tz == null))).Returns(FallbackToday);
    }

    // ════════════════════════════════════════════════════════════════
    // 第一層：解算器認得請求自己帶的時區
    // ════════════════════════════════════════════════════════════════

    private RelativePeriodResolver Resolver() => new(_dt.Object, _context);

    private static ReportRequestBase RequestIn(string? timeZoneId, RelativePeriod period)
        => new StubRequest { TimeZoneId = timeZoneId, Period = period };

    /// <summary>
    /// 呼叫端不傳時區時，解算器要退回 <c>request.TimeZoneId</c>——**而不是直接跳到行程預設**。
    ///
    /// <para>這一條就是缺陷本體。修正前 <c>ResolveAsync(request, ct: ct)</c> 把 <c>null</c>
    /// 一路傳到 <c>GetLocalDate(null)</c>，於是無論請求說自己屬於哪個廠，
    /// 解出來的都是 fallback 的那一天。</para>
    /// </summary>
    [Theory]
    [InlineData(FarEast, 2026, 5, 14)]   // 東邊的今天是 5/15 → 昨天 5/14
    [InlineData(FarWest, 2026, 5, 13)]   // 西邊的今天是 5/14 → 昨天 5/13
    public async Task 第一層_解算器要用請求自己帶的時區(string tz, int y, int m, int d)
    {
        var range = await Resolver().ResolveAsync(RequestIn(tz, RelativePeriod.Yesterday));

        Assert.Equal(new DateRange(new DateOnly(y, m, d), new DateOnly(y, m, d)), range);
    }

    /// <summary>
    /// 兩個廠的同一份「昨天」必須解出**不同的**日期。
    ///
    /// <para>上面那條逐一比對具體日期，這條比對的是「兩者不同」——
    /// 若有人把時區鏈改成某個固定值，逐值比對會紅在兩條，但這條的訊息
    /// 直接講出症狀：跨時區的兩個廠拿到了同一天。</para>
    /// </summary>
    [Fact]
    public async Task 第一層_兩個廠的昨天不得是同一天()
    {
        var east = await Resolver().ResolveAsync(RequestIn(FarEast, RelativePeriod.Yesterday));
        var west = await Resolver().ResolveAsync(RequestIn(FarWest, RelativePeriod.Yesterday));

        Assert.True(east != west,
            $"UTC+14 與 UTC−11 兩個廠解出了同一個「昨天」（{east}）——" +
            "時區沒有進到解算鏈裡，所有廠別都在用同一個日界。");
    }

    /// <summary>
    /// 顯式傳入的時區**優先於**請求自己帶的。順序寫反不會有任何徵兆：
    /// 兩者在單廠部署下永遠相同，只有跨廠排程才分得出來。
    /// </summary>
    [Fact]
    public async Task 第一層_顯式時區優先於請求帶的時區()
    {
        var range = await Resolver()
            .ResolveAsync(RequestIn(FarWest, RelativePeriod.Yesterday), FarEast);

        Assert.Equal(EastToday.AddDays(-1), range.From);
    }

    /// <summary>
    /// 兩者都沒有時才退到 <c>IDateTimeService</c> 自己的 fallback。
    /// 這是反面守門：把中間那層寫成「一律用 request 的值」會讓畫面路徑
    /// （request.TimeZoneId 恆為 null）從此拿不到使用者時區。
    /// </summary>
    [Fact]
    public async Task 第一層_兩者都沒有時才退回預設()
    {
        var range = await Resolver().ResolveAsync(RequestIn(null, RelativePeriod.Today));

        Assert.Equal(FallbackToday, range.From);
    }

    /// <summary>
    /// <c>AbsolutePeriod</c> 不受時區影響——絕對日期就是絕對日期。
    /// 反面守門：若有人把時區換算套到絕對期間上，「2026-05-01 到 05-31」會變成 4/30 起算。
    /// </summary>
    [Fact]
    public async Task 第一層_絕對期間不受時區影響()
    {
        var abs = new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        var request = new StubRequest { TimeZoneId = FarWest, AbsolutePeriod = abs };

        Assert.Equal(abs, await Resolver().ResolveAsync(request, FarEast));
    }

    // ════════════════════════════════════════════════════════════════
    // 第二層：runner 把時區注進請求
    // ════════════════════════════════════════════════════════════════

    private (ReportToolRunner Runner, CapturingTool Tool) RunnerWith()
    {
        var tool = new CapturingTool();
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();

        var runner = new ReportToolRunner(
            new ReportToolCatalog([tool], new Mock<IPermissionService>().Object), sp,
            ReportHeaderLocalizer.Create(), NewRunnerDb());

        return (runner, tool);
    }

    /// <summary>
    /// 工具拿到的請求要**已經知道**自己該用哪個時區。
    ///
    /// <para>注入點刻意放在 runner 而不是各工具內：要求 28 支工具每一支都記得把時區傳下去，
    /// 等於把正確性押在「下一個人會記得」上，而漏傳沒有任何徵兆。</para>
    /// </summary>
    [Fact]
    public async Task 第二層_runner把時區注進請求()
    {
        var (runner, tool) = RunnerWith();

        await runner.RunAsync(CapturingTool.Key, "{}", user: null, FarEast);

        Assert.Equal(FarEast, tool.Seen?.TimeZoneId);
    }

    /// <summary>
    /// 參數 JSON 裡已顯式指定時區時，**那個值贏**（<c>??=</c> 而非覆寫）。
    /// 「誰顯式講了就聽誰的」比「後寫的贏」容易在半年後看懂。
    /// </summary>
    [Fact]
    public async Task 第二層_參數裡顯式指定的時區不被覆寫()
    {
        var (runner, tool) = RunnerWith();

        await runner.RunAsync(
            CapturingTool.Key, $$"""{"timeZoneId":"{{FarWest}}"}""", user: null, FarEast);

        Assert.Equal(FarWest, tool.Seen?.TimeZoneId);
    }

    /// <summary>
    /// 不傳時區時維持原狀（<c>null</c>），畫面與 LLM 路徑因此行為不變——
    /// 它們有 HTTP 上下文，時區本來就由 <c>IUserContext</c> 供應。
    /// </summary>
    [Fact]
    public async Task 第二層_不傳時區時請求維持空值()
    {
        var (runner, tool) = RunnerWith();

        await runner.RunAsync(CapturingTool.Key, "{}", user: null, timeZoneId: null);

        Assert.Null(tool.Seen?.TimeZoneId);
    }

    /// <summary>
    /// 相對期間的排程要把**解出來的區間**交回去，派工器才寫得進執行紀錄。
    /// 2026-10-07 前只回 AbsolutePeriod，排程又一律存相對期間 → 執行紀錄的期間欄恆為空，
    /// 人工驗收 TZ-3 沒有東西可比對。用西邊時區驗，順便守住「記下的是廠別當地的昨天」。
    /// </summary>
    [Fact]
    public async Task 第二層_相對期間回報解出的區間供執行紀錄寫入()
    {
        var tool = new CapturingTool(requiresPeriod: true);
        var sp = new ServiceCollection().AddLogging()
            .AddSingleton<IRelativePeriodResolver>(Resolver()).BuildServiceProvider();
        var runner = new ReportToolRunner(
            new ReportToolCatalog([tool], new Mock<IPermissionService>().Object), sp,
            ReportHeaderLocalizer.Create(), NewRunnerDb());

        var result = await runner.RunAsync(
            CapturingTool.Key, """{"period":"Yesterday"}""", user: null, FarWest);

        var yesterday = WestToday.AddDays(-1);
        Assert.Equal(new DateRange(yesterday, yesterday), result.ResolvedRange);
    }

    /// <summary>不需要期間的工具（例如庫存快照）維持 null，收件匣不顯示期間。</summary>
    [Fact]
    public async Task 第二層_不需期間的工具不回報區間()
    {
        var (runner, _) = RunnerWith();

        var result = await runner.RunAsync(CapturingTool.Key, """{"period":"Yesterday"}""", user: null, FarWest);

        Assert.Null(result.ResolvedRange);
    }

    // ════════════════════════════════════════════════════════════════
    // 第三層：派工器解出廠別時區並一路傳到工具
    // ════════════════════════════════════════════════════════════════

    private sealed class NonOwningScopeFactory(IReadOnlyDictionary<Type, object> registry)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => registry.GetValueOrDefault(serviceType);
        public void Dispose() { }
    }

    /// <summary>派工器＋一個把收到的時區記下來的 runner 替身。</summary>
    private (ReportDispatcherService Dispatcher, List<string?> SeenTimeZones) Dispatcher()
    {
        var seen = new List<string?>();

        var permissions = new Mock<IReportSchedulePermissionChecker>();
        permissions.Setup(p => p.CheckOwnerAsync(It.IsAny<string>(), It.IsAny<string>(),
                                                 It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SchedulePermissionResult.Ok());
        permissions.Setup(p => p.CheckRecipientAsync(It.IsAny<string>(), It.IsAny<string>(),
                                                     It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SchedulePermissionResult.Ok());

        var runner = new Mock<IReportToolRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
                                     It.IsAny<ClaimsPrincipal?>(),
                                     It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .Callback((string _, string _, ClaimsPrincipal? _, string? tz, CancellationToken _) => seen.Add(tz))
              .ReturnsAsync(new ReportToolRunResult(["A"], [[1]], null));

        var files = new Mock<IReportFileStore>();
        files.Setup(f => f.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync("20260515/file.xlsx");

        var registry = new Dictionary<Type, object>
        {
            [typeof(ApplicationDbContext)] = _context,
            [typeof(IReportSchedulePermissionChecker)] = permissions.Object,
            [typeof(IReportToolRunner)] = runner.Object,
            [typeof(IReportFileStore)] = files.Object,
            [typeof(IExportService)] = new ExportService(),
        };

        return (new ReportDispatcherService(
                    new NonOwningScopeFactory(registry),
                    NullLogger<ReportDispatcherService>.Instance),
                seen);
    }

    private void SeedPlant(string id, string? timeZoneId)
    {
        _context.Plants.Add(new Plant { Id = id, Name = id, DefaultTimeZone = timeZoneId });
        _context.SaveChanges();
    }

    private void SeedOwner(string id, string? timeZoneId)
    {
        _context.Users.Add(new ApplicationUser { Id = id, UserName = id, TimeZoneId = timeZoneId });
        _context.SaveChanges();
    }

    private ReportDefinition SeedDue(string? plantId, string ownerUserId)
    {
        var definition = new ReportDefinition
        {
            Name = "日報", OwnerUserId = ownerUserId, OwnerRoleId = "role", ToolKey = "SalesRanking",
            ParamsJson = """{"period":"Yesterday"}""", PlantId = plantId,
            ScheduleKind = ReportScheduleKind.Daily, ScheduleHour = 8,
            NextRunAt = DateTime.UtcNow.AddMinutes(-1), IsActive = true, OutputFormat = "Excel",
            CreatedAt = DateTime.UtcNow,
        };
        _context.ReportDefinitions.Add(definition);
        _context.SaveChanges();
        return definition;
    }

    /// <summary>
    /// 排程實際執行時，傳給 runner 的是**廠別**時區——不是 null，也不是建立者的個人時區。
    ///
    /// <para>這是整條鏈的收口。前兩層各自正確但派工器不傳，缺陷就原封不動。</para>
    /// </summary>
    [Fact]
    public async Task 第三層_派工器傳的是廠別時區()
    {
        SeedPlant("P001", FarEast);
        SeedOwner("owner", FarWest);      // 個人時區刻意設成**不同**的那一個
        SeedDue("P001", "owner");

        var (dispatcher, seen) = Dispatcher();
        await dispatcher.ScanOnceAsync(default);

        Assert.Equal(FarEast, Assert.Single(seen));
    }

    /// <summary>廠別沒設時區時退到建立者個人時區——與 <c>IUserContext.TimeZoneId</c> 同一個順序。</summary>
    [Fact]
    public async Task 第三層_廠別未設時區時退回建立者個人時區()
    {
        SeedPlant("P001", null);
        SeedOwner("owner", FarWest);
        SeedDue("P001", "owner");

        var (dispatcher, seen) = Dispatcher();
        await dispatcher.ScanOnceAsync(default);

        Assert.Equal(FarWest, Assert.Single(seen));
    }

    /// <summary>
    /// 同一份排程，**查詢區間與下次執行時刻必須用同一個時區**。
    ///
    /// <para>兩邊各自解各自的話，可以在沒有任何徵兆的情況下答出不同時區，
    /// 而那個不對稱只有一半看得出來：下次執行時刻寫在排程清單上，
    /// 查詢區間只是報表少幾列。</para>
    /// </summary>
    [Fact]
    public async Task 第三層_查詢區間與下次執行時刻同源()
    {
        SeedPlant("P001", FarEast);
        SeedOwner("owner", FarWest);
        var definition = SeedDue("P001", "owner");

        var (dispatcher, seen) = Dispatcher();
        await dispatcher.ScanOnceAsync(default);

        var usedForQuery = Assert.Single(seen);
        var reloaded = (await _context.ReportDefinitions.FindAsync(definition.Id))!;

        var expected = ReportScheduleCalculator.ComputeNextRunUtc(
            reloaded, DateTime.UtcNow.AddMinutes(-1), usedForQuery);

        // 允許數分鐘誤差：ComputeNextRunUtc 以「現在」為基準，測試無法取到同一瞬間。
        // 差距若是**小時級**，就是兩邊用了不同時區。
        var drift = reloaded.NextRunAt!.Value - expected!.Value;
        Assert.True(Math.Abs(drift.TotalMinutes) < 5,
            $"下次執行時刻（{reloaded.NextRunAt}）與用同一個時區（{usedForQuery}）算出的" +
            $"時刻（{expected}）差了 {drift.TotalHours:0.0} 小時——查詢區間與排程推進用了不同時區。");
    }

    // ── 替身 ──────────────────────────────────────────────────────

    /// <summary>只有基底欄位的請求，用來驗解算鏈本身。</summary>
    private sealed class StubRequest : ReportRequestBase { }

    /// <summary>把收到的請求記下來的工具替身。</summary>
    private sealed class CapturingTool(bool requiresPeriod = false) : IReportToolDescriptor
    {
        internal const string Key = "TimeZoneProbe";

        internal ReportRequestBase? Seen { get; private set; }

        public string ToolKey => Key;
        public string Description => "驗時區注入用的替身";
        public ReportToolPermission Permission => ReportToolPermission.CanRead;
        public bool ContainsMonetaryValues => false;
        public bool RequiresPeriod => requiresPeriod;
        public Type RequestType => typeof(StubRequest);
        public Type ResponseType => typeof(IReadOnlyList<string>);

        public Task<IReadOnlyList<object>> GetExportRowsAsync(
            ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default)
        {
            Seen = request;
            return Task.FromResult<IReadOnlyList<object>>([]);
        }

        // 本組驗的是「請求拿到哪個時區」，與欄位無關；回空集合即可。
        // 零筆資料仍會輸出標題列，所以空欄位不會讓 Project 走到不同分支。
        public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) => [];
}
}
