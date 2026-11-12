using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 報表保留期清理（人工驗收 §10.2 R-9）。
///
/// <para><b>為什麼補這一批</b>：`manual-test-plan.md` §10.2 的自動化覆蓋表把「保留期清理」
/// 標成「<b>無</b>」——那是 Wave R 十一項裡**唯一連自動化都沒有**的一項，而它要驗的
/// 「不得留下孤兒檔」正好是機器做得比人好的那種：人要去比對檔案系統與資料表兩邊，
/// 機器只是兩個斷言。</para>
///
/// <para>R-9 的通過標準原文是「超過保留天數的產出檔與紀錄一起消失；<b>不得留下孤兒檔</b>
/// （紀錄刪了檔還在＝含機密資料的檔從此沒人清理）」。這批測試就是照那句話寫的。</para>
/// </summary>
public class ReportRetentionJobServiceTests : TestBase
{
    private readonly Mock<IReportFileStore> _files = new();

    private ReportRetentionJobService NewService(int? retentionDays = null)
    {
        var settings = new Dictionary<string, string?>();
        if (retentionDays is { } d)
            settings["Reports:RetentionDays"] = d.ToString();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // 服務自己開 scope 取 DbContext 與 IReportFileStore，所以要餵它一個真的容器。
        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_files.Object);

        return new ReportRetentionJobService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<ReportRetentionJobService>.Instance);
    }

    private void AddRun(long id, DateTime runAt, string? filePath)
    {
        _context.ReportRuns.Add(new ReportRun
        {
            RunId        = id,
            DefinitionId = 1,
            RunAt        = runAt,
            TriggerType  = ReportTriggerType.Scheduled,
            Status       = ReportRunStatus.Success,
            FilePath     = filePath,
        });
        _context.SaveChanges();
    }

    // ── 保留天數的解析 ────────────────────────────────────────────

    /// <summary>未設定時取預設 90 天。**刻意寫死 90 而不是引用那個常數**：
    /// 引用常數的話，把常數改掉這條測試會跟著改、什麼都擋不到。</summary>
    [Fact]
    public void 保留天數_未設定時取預設90天()
        => Assert.Equal(90, NewService().ResolveRetentionDays());

    /// <summary>
    /// 設定 0 或負數時退回預設，**不是**「0 天＝全部刪掉」。
    /// 把這一行改成直接採用設定值，一個手滑的 `0` 就會在半夜清空整張表。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 保留天數_非正數退回預設(int configured)
        => Assert.Equal(90, NewService(configured).ResolveRetentionDays());

    /// <summary>設定正數時採用設定值。</summary>
    [Fact]
    public void 保留天數_採用設定的正數()
        => Assert.Equal(7, NewService(7).ResolveRetentionDays());

    // ── 清理行為 ──────────────────────────────────────────────────

    /// <summary>
    /// 超過保留期的紀錄與檔案一起消失；保留期內的兩者都不動。
    /// 邊界方向是 <c>RunAt &lt; cutoff</c>——剛好等於 cutoff 的那一筆留著。
    /// </summary>
    [Fact]
    public async Task 清理_過期的刪掉未過期的留著()
    {
        var now = DateTime.UtcNow;
        AddRun(1, now.AddDays(-100), "old/a.xlsx");     // 過期
        AddRun(2, now.AddDays(-10),  "new/b.xlsx");     // 保留期內

        var deleted = await NewService(90).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        _files.Verify(f => f.Delete("old/a.xlsx"), Times.Once);
        _files.Verify(f => f.Delete("new/b.xlsx"), Times.Never);

        var remaining = await _context.ReportRuns.AsNoTracking().Select(r => r.RunId).ToListAsync();
        Assert.Equal(new long[] { 2 }, remaining);
    }

    /// <summary>
    /// 保留天數由設定決定：同一批資料在 7 天保留期下會多刪一筆。
    /// 只驗預設值的話，設定根本沒接上也看不出來。
    /// </summary>
    [Fact]
    public async Task 清理_保留天數跟著設定走()
    {
        var now = DateTime.UtcNow;
        AddRun(1, now.AddDays(-100), "a.xlsx");
        AddRun(2, now.AddDays(-10),  "b.xlsx");

        var deleted = await NewService(7).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Empty(await _context.ReportRuns.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// 沒有產出檔的紀錄（失敗、零筆略過）仍要隨紀錄一起清掉，且不得去呼叫 Delete。
    /// </summary>
    [Fact]
    public async Task 清理_無檔案的紀錄照樣清掉且不呼叫刪檔()
    {
        AddRun(1, DateTime.UtcNow.AddDays(-100), null);
        AddRun(2, DateTime.UtcNow.AddDays(-100), "   ");

        var deleted = await NewService(90).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        _files.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        Assert.Empty(await _context.ReportRuns.AsNoTracking().ToListAsync());
    }

    /// <summary>沒有任何過期紀錄時不寫 DB、不刪檔，回 0。</summary>
    [Fact]
    public async Task 清理_沒有過期紀錄時什麼都不做()
    {
        AddRun(1, DateTime.UtcNow.AddDays(-1), "a.xlsx");

        Assert.Equal(0, await NewService(90).RunOnceAsync(CancellationToken.None));
        _files.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        Assert.Single(await _context.ReportRuns.AsNoTracking().ToListAsync());
    }

    // ── R-9 的核心：不得留下孤兒檔 ────────────────────────────────

    /// <summary>
    /// <b>刪檔失敗的那一筆，紀錄必須留著。</b>
    ///
    /// <para>這是 R-9 通過標準裡唯一一句加粗的話：「不得留下孤兒檔（紀錄刪了檔還在
    /// ＝含機密資料的檔從此沒人清理）」。紀錄是下一輪清理**唯一**知道那個檔存在的憑據——
    /// 紀錄一刪，檔案就從所有清單上消失，只剩檔案系統裡一份沒有人會再碰的機密報表。</para>
    ///
    /// <para>服務自己的註解也是這樣寫的：「這一筆下一輪會再試（紀錄還在，所以不會變成孤兒）」。
    /// 這條測試就是驗那句話。</para>
    /// </summary>
    [Fact]
    public async Task 清理_刪檔失敗時該筆紀錄必須留著()
    {
        var now = DateTime.UtcNow;
        AddRun(1, now.AddDays(-100), "locked.xlsx");
        AddRun(2, now.AddDays(-100), "ok.xlsx");

        _files.Setup(f => f.Delete("locked.xlsx")).Throws(new IOException("檔案被佔用"));

        var deleted = await NewService(90).RunOnceAsync(CancellationToken.None);

        // 刪得掉的那一筆照常清掉
        _files.Verify(f => f.Delete("ok.xlsx"), Times.Once);

        // 刪不掉的那一筆，紀錄必須留下來，否則檔案就成了孤兒
        var remaining = await _context.ReportRuns.AsNoTracking().Select(r => r.RunId).ToListAsync();
        Assert.Equal(new long[] { 1 }, remaining);
        Assert.Equal(1, deleted);
    }

    /// <summary>
    /// 承上：留下來的紀錄，**下一輪真的會再試一次**。
    /// 只驗「紀錄還在」還不夠——若下一輪因為某種原因跳過它，那份檔仍舊沒人清。
    /// </summary>
    [Fact]
    public async Task 清理_留下來的紀錄下一輪會再試()
    {
        AddRun(1, DateTime.UtcNow.AddDays(-100), "locked.xlsx");

        var svc = NewService(90);

        _files.Setup(f => f.Delete("locked.xlsx")).Throws(new IOException("檔案被佔用"));
        Assert.Equal(0, await svc.RunOnceAsync(CancellationToken.None));
        Assert.Single(await _context.ReportRuns.AsNoTracking().ToListAsync());

        // 下一輪：障礙排除
        _files.Reset();
        Assert.Equal(1, await svc.RunOnceAsync(CancellationToken.None));
        _files.Verify(f => f.Delete("locked.xlsx"), Times.Once);
        Assert.Empty(await _context.ReportRuns.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// 單一檔案刪不掉**不得中止整輪**——其餘檔案仍要清掉。
    /// 少了 try/catch 的話，一份被防毒軟體鎖住的檔就能讓保留期清理從此永遠不完成，
    /// 而那是靜默的：排程紀錄只會看到一次例外。
    /// </summary>
    [Fact]
    public async Task 清理_單檔失敗不中止整輪()
    {
        var now = DateTime.UtcNow;
        AddRun(1, now.AddDays(-100), "a.xlsx");
        AddRun(2, now.AddDays(-100), "locked.xlsx");
        AddRun(3, now.AddDays(-100), "c.xlsx");

        _files.Setup(f => f.Delete("locked.xlsx")).Throws(new IOException("檔案被佔用"));

        var deleted = await NewService(90).RunOnceAsync(CancellationToken.None);

        _files.Verify(f => f.Delete("a.xlsx"), Times.Once);
        _files.Verify(f => f.Delete("c.xlsx"), Times.Once);
        Assert.Equal(2, deleted);

        var remaining = await _context.ReportRuns.AsNoTracking().Select(r => r.RunId).ToListAsync();
        Assert.Equal(new long[] { 2 }, remaining);
    }

    // ── 排程時刻 ──────────────────────────────────────────────────

    /// <summary>
    /// 每日觸發時刻為 04:00（服務註解說是為了錯開稽核 02:00 與稽核日誌清理 03:00）。
    ///
    /// <para>⚠️ <b>但它其實沒有完全錯開</b>：<c>PasswordHistoryRetentionJobService</c>
    /// 也取 04:00，而它的註解同樣寫著「錯開每日稽核(02:00)與 AuditLog 清理(03:00)」——
    /// 兩支各自避開了前兩支，卻沒有避開彼此。本測試**鎖住現況而不是主張現況是對的**：
    /// 改排程時刻是營運決定，不該由一條測試順手改掉；這裡只保證它不會無聲地漂走。</para>
    /// </summary>
    [Fact]
    public void 觸發時刻_維持在凌晨4點()
    {
        var svc = NewService();
        var runAtHour = typeof(SmartFactory.MES.Services.Jobs.DailyJobServiceBase)
            .GetProperty("RunAtHour",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(svc);

        Assert.Equal(4, runAtHour);
    }
}
