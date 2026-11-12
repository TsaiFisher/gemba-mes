using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 報表排程派工器（§7.1 定案 #1：方案 A，<c>BackgroundService</c>，零新依賴）。
///
/// <para>每分鐘掃一次到期的定義。**不是每分鐘跑報表**——掃描是一句
/// <c>NextRunAt &lt;= now</c> 的索引查詢，沒有到期的定義時什麼都不做。</para>
///
/// <para><b>三個容易寫錯而且錯了不會有徵兆的地方：</b></para>
/// <list type="number">
///   <item><b>不補跑</b>（§7.6）：停機期間錯過的排程只記 <c>Skipped</c>，不追跑。
///         <c>LastMonth</c> 補跑結果相同（無害），<c>Yesterday</c> 補跑卻會拿到錯的一天——
///         同一個補跑策略對不同 <c>RelativePeriod</c> 語意不一致，與其做分類邏輯不如不補。</item>
///   <item><b>執行前重查權限</b>（§7.8 S1）：不是建立時查一次。</item>
///   <item><b>依收件人語系去重產檔</b>（§7.3 定案 #16）：不是每人一份，也不是
///         「owner 語系一份」。後者會讓英文使用者固定收到中文報表，而數字是對的，
///         所以永遠不會有人回報。</item>
/// </list>
///
/// <para>單機部署假設（§7.7）。日後 scale-out 時 <c>ReportRun</c> 可直接當分散式鎖：
/// 對 <c>(DefinitionId, 預定執行時刻)</c> 建唯一索引，多副本搶插入只有一個會成功。</para>
/// </summary>
public sealed class ReportDispatcherService : BackgroundService
{
    /// <summary>掃描間隔。一分鐘足夠——排程精度本來就只到分鐘。</summary>
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportDispatcherService> _logger;

    public ReportDispatcherService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReportDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動時留一行。既有的三支 MFG 背景服務都這麼做，理由在這裡更強：
        // 派工器沒跑起來的症狀是「排程都沒送出」，而那與「沒有到期的排程」長得一模一樣。
        _logger.LogInformation("報表派工器已啟動（掃描間隔 {Interval} 分鐘）", ScanInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // 單次掃描失敗不得讓整個背景服務結束——那會讓排程從此靜默停擺，
                // 而且因為沒有例外往外拋，沒有任何監控會發現。
                _logger.LogError(ex, "報表派工器掃描失敗，將於下一輪重試");
            }
        }
    }

    /// <summary>
    /// 掃描一輪。<c>public</c> 是為了讓測試與「立即執行」之類的手動觸發都能直接呼叫，
    /// 不必等背景迴圈醒來——背景迴圈本身只負責定時，邏輯全在這裡。
    /// </summary>
    public async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;

        // 只取到期且啟用中的排程。ScheduleKind 為 null 的是純常用查詢，永遠不該被派工。
        // 按了「立即執行」的不看時間：RunNow 把 NextRunAt 設成按下當時的時鐘，系統時鐘若在那之後
        // 回撥（WSL 實測一輪數次、每次數秒），「現在」反而比它早，這一輪就漏掉、要等時鐘追上才跑。
        // 旗標在 AdvanceAsync 無論成敗都會清掉，不會重複執行。
        var due = await db.ReportDefinitions
            .Where(d => d.IsActive
                     && d.ScheduleKind != null
                     && (d.ManualRunRequested || (d.NextRunAt != null && d.NextRunAt <= now)))
            .OrderBy(d => d.NextRunAt)
            .ToListAsync(ct);

        foreach (var definition in due)
        {
            if (ct.IsCancellationRequested) break;

            // 一份排程**只解一次**時區，「報表查哪一段」與「下次幾點再跑」共用同一個答案。
            // 兩邊各解各的就有機會答出不同時區，而那不對稱只有一半看得出來：
            // 下次執行時刻寫在畫面上，查詢區間只是少幾列（§10.3 TZ-3）。
            // 「有人按了立即執行」與「排程自己到期」在這裡是同一件事——NextRunAt 到了。
            // 兩者的差別只存在於 RunNow 留下的這個旗標，所以要在**執行之前**取值，
            // 並且讓這一輪產出的每一筆紀錄（含失敗與略過）都帶著同一個答案。
            var triggerType = definition.ManualRunRequested
                ? ReportTriggerType.Manual
                : ReportTriggerType.Scheduled;

            string? timeZoneId = null;
            try
            {
                timeZoneId = await ReportScheduleTimeZoneResolver.ResolveAsync(db, definition, ct);
                await ExecuteDefinitionAsync(scope.ServiceProvider, db, definition, now, timeZoneId,
                                             triggerType, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "報表排程 {DefinitionId} 執行失敗", definition.Id);
                await RecordAsync(db, definition, ReportRunStatus.Failed, now, ex.Message, triggerType, ct);
            }
            finally
            {
                // 無論成敗都要往前推 NextRunAt。少了這一步，失敗的排程會在下一輪、
                // 再下一輪繼續被撈出來重試，把日誌灌爆而且永遠不會前進。
                // timeZoneId 為 null（連時區都沒解成）時由計算器退回 FallbackTimeZoneId——
                // 推不準好過推不動。
                //
                // 手動旗標也在這裡清，理由相同：**無論成敗都要清**。
                // 只在成功路徑清的話，一次失敗的手動執行會讓這筆排程從此每一輪都標成手動。
                await AdvanceAsync(db, definition, now, timeZoneId, ct);
            }
        }
    }

    private async Task ExecuteDefinitionAsync(
        IServiceProvider services, ApplicationDbContext db,
        ReportDefinition definition, DateTime now, string? timeZoneId,
        string triggerType, CancellationToken ct)
    {
        var permissions = services.GetRequiredService<IReportSchedulePermissionChecker>();

        // ── §7.8 S1：每次執行都重查 owner 權限 ─────────────────────────────
        var ownerCheck = await permissions.CheckOwnerAsync(
            definition.OwnerUserId, definition.OwnerRoleId, definition.ToolKey, ct);

        if (!ownerCheck.Granted)
        {
            // §7.8 S2：停用 ＋ 留下原因。不得靜默續寄，也不得靜默停止——
            // 兩者都會讓使用者以為系統壞了，而管理者查不到任何線索。
            definition.IsActive = false;
            await RecordAsync(db, definition, ReportRunStatus.SkippedNoPermission,
                              now, ownerCheck.Reason, triggerType, ct);

            _logger.LogWarning("報表排程 {DefinitionId} 因權限失效停用：{Reason}",
                               definition.Id, ownerCheck.Reason);
            return;
        }

        // ── §7.8 S4：授權範圍由 OwnerRoleId 顯式設定進 scope ────────────────
        // 沒有這三行，global query filter 會以空上下文求值，工具**靜默查到 0 筆**，
        // 而且產出的 Excel 是一份格式正確的空報表——不會有任何錯誤。
        var owner = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == definition.OwnerUserId, ct);

        db.CurrentPlantId  = definition.PlantId;
        db.CurrentRoleId   = definition.OwnerRoleId;
        db.CurrentRolePath = await ResolveRolePathAsync(db, definition.OwnerRoleId, ct);

        // ── §7.8 S3：逐一檢查收件人，並依語系分組（§7.3 定案 #16）──────────
        var recipients = await db.ReportRecipients.AsNoTracking()
            .Where(r => r.DefinitionId == definition.Id)
            .ToListAsync(ct);

        var ownerLanguage = string.IsNullOrWhiteSpace(owner?.Language) ? "zh-TW" : owner!.Language!;

        var languages = new List<string>();
        foreach (var recipient in recipients)
        {
            var check = await permissions.CheckRecipientAsync(recipient.UserId, definition.ToolKey, ct);
            if (!check.Granted)
            {
                // 收件人失格不停整份排程——那會讓一個人的異動害所有人收不到。
                // 只是他不列入語系分組，於是不會有他語系的那一份被產出來。
                _logger.LogWarning("報表排程 {DefinitionId} 略過收件人 {UserId}：{Reason}",
                                   definition.Id, recipient.UserId, check.Reason);
                continue;
            }

            languages.Add(string.IsNullOrWhiteSpace(recipient.Language) ? ownerLanguage : recipient.Language!);
        }

        // 沒有收件人的排程＝owner 自己看，產一份他的語系。
        var distinctLanguages = languages.Count == 0
            ? [ownerLanguage]
            : languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var language in distinctLanguages)
            await ProduceAsync(services, db, definition, language, now, timeZoneId, triggerType, ct);
    }

    /// <summary>產出單一語系的一份檔案，並記一列 <see cref="ReportRun"/>。</summary>
    private async Task ProduceAsync(
        IServiceProvider services, ApplicationDbContext db,
        ReportDefinition definition, string language, DateTime now,
        string? timeZoneId, string triggerType, CancellationToken ct)
    {
        var runner = services.GetRequiredService<IReportToolRunner>();
        var exporter = services.GetRequiredService<IExportService>();
        var files = services.GetRequiredService<IReportFileStore>();

        var stopwatch = Stopwatch.StartNew();

        // 排程沒有請求管線，CurrentUICulture 會是行程預設值。不顯式設定的話，
        // 英文收件人會收到中文標題的 Excel——數字正確，所以沒人會回報這算 bug（§7.3）。
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            // 語系代碼壞掉時維持行程預設，不讓一筆壞資料擋掉整份報表。
            _logger.LogWarning("報表排程 {DefinitionId} 的語系 {Language} 無效，改用行程預設",
                               definition.Id, language);
        }

        try
        {
            var result = await runner.RunAsync(
                definition.ToolKey, definition.ParamsJson, user: null, timeZoneId, ct);

            var isCsv = string.Equals(definition.OutputFormat, "Csv", StringComparison.OrdinalIgnoreCase);
            var request = result.ToExportRequest(definition.Name);
            var bytes = isCsv ? exporter.ToCsv(request) : exporter.ToExcel(request);

            var relativePath = await files.SaveAsync(bytes, isCsv ? ".csv" : ".xlsx", ct);

            stopwatch.Stop();
            db.ReportRuns.Add(new ReportRun
            {
                DefinitionId = definition.Id,
                RunAt        = now,
                TriggerType  = triggerType,
                Status       = ReportRunStatus.Success,
                ResolvedFrom = result.ResolvedRange?.From,
                ResolvedTo   = result.ResolvedRange?.To,
                RowCount     = result.Rows.Count,
                DurationMs   = stopwatch.ElapsedMilliseconds,
                FilePath     = relativePath,
                Language     = language,
                CreatedBy    = "SYSTEM",
            });
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    /// <summary>
    /// 算出下一次執行時刻。**錯過的那些不補跑**（§7.6）——直接以「現在」為基準往後算，
    /// 而不是從舊的 <c>NextRunAt</c> 逐次加上去把積欠的補完。
    /// </summary>
    private static async Task AdvanceAsync(
        ApplicationDbContext db, ReportDefinition definition,
        DateTime now, string? timeZoneId, CancellationToken ct)
    {
        // 時區一律走 ReportScheduleTimeZoneResolver（廠別時區 → 建立者個人時區 → fallback），
        // 與存檔那條路徑用同一份邏輯。原本這裡只讀建立者的個人欄位，繞過了廠別優先的鏈，
        // 於是同一份「每日 08:00」換個人建立就在不同時刻寄出（manual-test-plan §11 #10）。
        // 2026-09-15 起由呼叫端解好傳進來，好讓這一份排程的查詢區間與下次執行時刻**同源**。
        definition.NextRunAt = ReportScheduleCalculator.ComputeNextRunUtc(definition, now, timeZoneId);
        definition.ManualRunRequested = false;
        await db.SaveChangesAsync(ct);
    }

    private static async Task RecordAsync(
        ApplicationDbContext db, ReportDefinition definition,
        string status, DateTime now, string? message, string triggerType, CancellationToken ct)
    {
        db.ReportRuns.Add(new ReportRun
        {
            DefinitionId = definition.Id,
            RunAt        = now,
            TriggerType  = triggerType,
            Status       = status,
            ErrorMessage = Truncate(message, 1000),
            CreatedBy    = "SYSTEM",
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ResolveRolePathAsync(
        ApplicationDbContext db, string roleId, CancellationToken ct)
        => await db.Roles.AsNoTracking()
            .Where(r => r.Id == roleId)
            .Select(r => r.OrganizationUnit != null ? r.OrganizationUnit.Path : null)
            .FirstOrDefaultAsync(ct);

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
