using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 派工器（§7.1／§7.6／§7.8）。
///
/// <para>這裡驗的每一條都屬於「錯了不會有徵兆」那一類：排程不執行、報表少一份、
/// 或是權限失效後繼續寄——三者都不會拋例外，也不會出現在任何錯誤畫面上。</para>
/// </summary>
public class ReportDispatcherServiceTests : TestBase
{
    private readonly Mock<IReportSchedulePermissionChecker> _permissions = new();
    private readonly Mock<IReportToolRunner> _runner = new();
    private readonly Mock<IReportFileStore> _files = new();

    public ReportDispatcherServiceTests()
    {
        // 預設：一切正常。個別測試再覆寫成失敗情境。
        _permissions.Setup(p => p.CheckOwnerAsync(It.IsAny<string>(), It.IsAny<string>(),
                                                  It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(SchedulePermissionResult.Ok());

        _permissions.Setup(p => p.CheckRecipientAsync(It.IsAny<string>(), It.IsAny<string>(),
                                                      It.IsAny<CancellationToken>()))
                    .ReturnsAsync(SchedulePermissionResult.Ok());

        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
                                      It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                                      It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ReportToolRunResult(["A"], [[1]], null));

        _files.Setup(f => f.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync("20260812/file.xlsx");
    }

    private ReportDispatcherService CreateDispatcher()
    {
        // ⚠️ 不能用 ServiceCollection.BuildServiceProvider()：把 _context 註冊進去之後，
        // 容器會在 scope 結束時把它 Dispose 掉，於是測試方法後面的斷言（以及 TestBase.Dispose）
        // 全部拿到 ObjectDisposedException。這裡用一個「不擁有任何物件」的假 scope factory，
        // 讓 context 的生命週期仍由 TestBase 管。
        var registry = new Dictionary<Type, object>
        {
            [typeof(ApplicationDbContext)] = _context,
            [typeof(IReportSchedulePermissionChecker)] = _permissions.Object,
            [typeof(IReportToolRunner)] = _runner.Object,
            [typeof(IReportFileStore)] = _files.Object,
            [typeof(IExportService)] = new ExportService(),
        };

        return new ReportDispatcherService(
            new NonOwningScopeFactory(registry),
            NullLogger<ReportDispatcherService>.Instance);
    }

    /// <summary>解析預先建好的實例，且 <c>Dispose</c> 什麼都不做。</summary>
    private sealed class NonOwningScopeFactory(IReadOnlyDictionary<Type, object> registry)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => registry.GetValueOrDefault(serviceType);
        public void Dispose() { }
    }

    private ApplicationUser SeedUser(string id, string? language = null, string? timeZoneId = null)
    {
        var user = new ApplicationUser
        {
            Id = id, UserName = id, Language = language, TimeZoneId = timeZoneId ?? "Asia/Taipei",
        };
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    private ReportDefinition SeedSchedule(
        DateTime? nextRunAt, ReportScheduleKind? kind = ReportScheduleKind.Daily, bool isActive = true)
    {
        var definition = new ReportDefinition
        {
            Name = "月報", OwnerUserId = "owner", OwnerRoleId = "role", ToolKey = "SalesRanking",
            ParamsJson = "{}", ScheduleKind = kind, ScheduleHour = 9,
            NextRunAt = nextRunAt, IsActive = isActive, OutputFormat = "Excel",
            CreatedAt = DateTime.UtcNow,
        };
        _context.ReportDefinitions.Add(definition);
        _context.SaveChanges();
        return definition;
    }

    [Fact]
    public async Task 未到期的排程不執行()
    {
        SeedUser("owner");
        SeedSchedule(DateTime.UtcNow.AddHours(1));

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Empty(_context.ReportRuns);
    }

    [Fact]
    public async Task 純常用查詢永遠不被派工()
    {
        // ScheduleKind = null 是常用查詢。它的 NextRunAt 恆為 null，
        // 但即使有人手動塞了一個過去的時刻，也不該被執行。
        SeedUser("owner");
        SeedSchedule(DateTime.UtcNow.AddHours(-1), kind: null);

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Empty(_context.ReportRuns);
    }

    [Fact]
    public async Task 已停用的排程不執行()
    {
        SeedUser("owner");
        SeedSchedule(DateTime.UtcNow.AddHours(-1), isActive: false);

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Empty(_context.ReportRuns);
    }

    [Fact]
    public async Task 到期執行成功會留下紀錄與檔案路徑()
    {
        SeedUser("owner");
        SeedSchedule(DateTime.UtcNow.AddMinutes(-1));

        await CreateDispatcher().ScanOnceAsync(default);

        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal(ReportRunStatus.Success, run.Status);
        Assert.Equal(ReportTriggerType.Scheduled, run.TriggerType);
        Assert.Equal("20260812/file.xlsx", run.FilePath);
        Assert.Equal(1, run.RowCount);
    }

    [Fact]
    public async Task Owner權限失效時停用排程並記錄原因()
    {
        // §7.8 S2：不得靜默續寄，也不得靜默停止。兩者都會讓管理者查不到任何線索。
        _permissions.Setup(p => p.CheckOwnerAsync(It.IsAny<string>(), It.IsAny<string>(),
                                                  It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(SchedulePermissionResult.Denied("擁有者已不屬於該角色"));

        SeedUser("owner");
        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));

        await CreateDispatcher().ScanOnceAsync(default);

        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal(ReportRunStatus.SkippedNoPermission, run.Status);
        Assert.Equal("擁有者已不屬於該角色", run.ErrorMessage);

        Assert.False((await _context.ReportDefinitions.FindAsync(definition.Id))!.IsActive);

        // 權限失效時不該產生任何檔案。
        _files.Verify(f => f.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                      Times.Never);
    }

    [Fact]
    public async Task 收件人語系去重_中英混合產兩份()
    {
        // §7.3 定案 #16：不是每人一份，也不是 owner 語系一份。
        SeedUser("owner", language: "zh-TW");
        SeedUser("a", language: "zh-TW");
        SeedUser("b", language: "zh-TW");
        SeedUser("c", language: "en-US");

        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));
        _context.ReportRecipients.AddRange(
            new ReportRecipient { DefinitionId = definition.Id, UserId = "a", Language = "zh-TW" },
            new ReportRecipient { DefinitionId = definition.Id, UserId = "b", Language = "zh-TW" },
            new ReportRecipient { DefinitionId = definition.Id, UserId = "c", Language = "en-US" });
        _context.SaveChanges();

        await CreateDispatcher().ScanOnceAsync(default);

        var runs = await _context.ReportRuns.ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(["en-US", "zh-TW"], runs.Select(r => r.Language).OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// 分組分對了還不夠——**語系要真的套到執行緒上**，工具才會拿到對的欄頭。
    ///
    /// <para>🔴 2026-09-17 補：這一段原本完全沒有斷言。派工器在呼叫工具前設
    /// <c>CultureInfo.CurrentUICulture</c>，實測把那一行改成不套用，
    /// RPS 篩選 217 支全綠——因為上下兩層都測得很厚，中間這一步沒有人看。
    /// 症狀是英文收件人收到中文標題的 Excel：<b>數字全對，所以沒人會回報這算 bug</b>。</para>
    ///
    /// <para>攔在 runner 的呼叫當下，而不是事後看 <c>run.Language</c>：
    /// 後者只證明「分組時知道語系」，證明不了「工具跑的時候語系是對的」。</para>
    /// </summary>
    [Fact]
    public async Task 呼叫工具當下的語系_就是那份報表的語系()
    {
        SeedUser("owner", language: "zh-TW");
        SeedUser("a", language: "zh-TW");
        SeedUser("c", language: "en-US");

        var seen = new List<string>();
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
                                      It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                                      It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ReportToolRunResult(["A"], [[1]], null))
               .Callback(() => seen.Add(CultureInfo.CurrentUICulture.Name));

        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));
        _context.ReportRecipients.AddRange(
            new ReportRecipient { DefinitionId = definition.Id, UserId = "a", Language = "zh-TW" },
            new ReportRecipient { DefinitionId = definition.Id, UserId = "c", Language = "en-US" });
        _context.SaveChanges();

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Equal(["en-US", "zh-TW"], seen.OrderBy(x => x).ToArray());

        // ⚠️ 這裡**刻意不驗**「跑完有沒有把語系還原回去」（派工器 finally 的那一行）。
        // 試過兩種寫法都沒有區辨力，第二種是量出來的：先把進場語系設成兩份報表都不是的
        // ja-JP，再於掃描後比對——把 finally 的還原拿掉，這一支照樣綠，而且掃完之後
        // 測試執行緒上讀到的仍然是 ja-JP。也就是**派工器設的語系從頭到尾沒有到達呼叫端**，
        // 「有沒有還原」在這一層觀察不到，寫出來的會是一句永遠不紅的斷言。
        // 與其留一個看起來有守、實際上守不到的斷言，不如寫清楚它為什麼不在這裡。
    }

    [Fact]
    public async Task 收件人未指定語系時沿用Owner語系()
    {
        SeedUser("owner", language: "en-US");
        SeedUser("a");

        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));
        _context.ReportRecipients.Add(
            new ReportRecipient { DefinitionId = definition.Id, UserId = "a", Language = null });
        _context.SaveChanges();

        await CreateDispatcher().ScanOnceAsync(default);

        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal("en-US", run.Language);
    }

    [Fact]
    public async Task 收件人失格只略過該人不停整份排程()
    {
        // 一個人的異動不該害所有人收不到。
        _permissions.Setup(p => p.CheckRecipientAsync("blocked", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(SchedulePermissionResult.Denied("收件人無檢視權限"));

        SeedUser("owner", language: "zh-TW");
        SeedUser("ok", language: "zh-TW");
        SeedUser("blocked", language: "en-US");

        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));
        _context.ReportRecipients.AddRange(
            new ReportRecipient { DefinitionId = definition.Id, UserId = "ok", Language = "zh-TW" },
            new ReportRecipient { DefinitionId = definition.Id, UserId = "blocked", Language = "en-US" });
        _context.SaveChanges();

        await CreateDispatcher().ScanOnceAsync(default);

        // 只產出中文那一份；英文那份因為唯一的收件人失格而不存在。
        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal(ReportRunStatus.Success, run.Status);
        Assert.Equal("zh-TW", run.Language);
        Assert.True((await _context.ReportDefinitions.FindAsync(definition.Id))!.IsActive);
    }

    [Fact]
    public async Task 沒有收件人時仍以Owner語系產一份()
    {
        SeedUser("owner", language: "zh-TW");
        SeedSchedule(DateTime.UtcNow.AddMinutes(-1));

        await CreateDispatcher().ScanOnceAsync(default);

        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal("zh-TW", run.Language);
    }

    [Fact]
    public async Task 錯過多次的排程不補跑_只往前排下一次()
    {
        // §7.6：停機三天後，LastMonth 補跑無害但 Yesterday 補跑會拿到錯的一天。
        // 與其做分類邏輯不如不補——所以只該產生**一份**，而不是把積欠的三天補完。
        SeedUser("owner");
        var definition = SeedSchedule(DateTime.UtcNow.AddDays(-3));

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Single(_context.ReportRuns);

        var reloaded = (await _context.ReportDefinitions.FindAsync(definition.Id))!;
        Assert.NotNull(reloaded.NextRunAt);
        Assert.True(reloaded.NextRunAt > DateTime.UtcNow,
            "NextRunAt 必須以「現在」為基準往後排；沿用舊時刻逐次累加等於補跑。");
    }

    [Fact]
    public async Task 執行失敗仍要推進NextRunAt否則會無限重試()
    {
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
                                      It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                                      It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ReportParamsInvalidException("SalesRanking", "欄位已移除"));

        SeedUser("owner");
        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));

        await CreateDispatcher().ScanOnceAsync(default);

        var run = Assert.Single(_context.ReportRuns);
        Assert.Equal(ReportRunStatus.Failed, run.Status);
        Assert.Contains("欄位已移除", run.ErrorMessage);

        var reloaded = (await _context.ReportDefinitions.FindAsync(definition.Id))!;
        Assert.True(reloaded.NextRunAt > DateTime.UtcNow,
            "失敗的排程若不推進 NextRunAt，會在每一輪被重複撈出並灌爆日誌。");

        // 失敗不等於權限問題，不該停用。
        Assert.True(reloaded.IsActive);
    }

    [Fact]
    public async Task 執行時會把授權範圍設進DbContext上下文()
    {
        // §7.8 S4。少了這一步，global query filter 以空上下文求值，
        // 工具靜默查到 0 筆，產出一份格式正確的空報表——不會有任何錯誤。
        SeedUser("owner");
        var definition = SeedSchedule(DateTime.UtcNow.AddMinutes(-1));
        definition.PlantId = "P1";
        _context.SaveChanges();

        await CreateDispatcher().ScanOnceAsync(default);

        Assert.Equal("P1", _context.CurrentPlantId);
        Assert.Equal("role", _context.CurrentRoleId);
    }
}
