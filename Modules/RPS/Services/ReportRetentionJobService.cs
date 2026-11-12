using SmartFactory.MES.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Models;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 報表產出的保留期清理（§7.5）。比照既有 <c>AuditLogRetentionJobService</c> 的範式：
/// <c>IConfiguration</c> 讀天數 ＋ <c>ResolveRetentionDays()</c> 純函式 ＋ 每日固定時刻。
///
/// <para><b>執行時刻刻意錯開既有排程</b>：每日稽核 02:00、稽核日誌清理 03:00，
/// 本服務取 04:00。三個都在同一時刻醒來會讓 DB 在半夜出現一個不必要的尖峰，
/// 而那個尖峰只有在出事時才會被注意到。</para>
///
/// <para><b>先刪檔再刪紀錄</b>：順序反過來的話，一旦中途失敗就會留下沒有任何紀錄
/// 指向的孤兒檔案，那些檔案從此不會有人清理，而且它們含機密資料。</para>
///
/// <para>⚠️ 保留期與備份的關係見 <c>deploy/file-backup.md</c>：備份保留期若長於本設定，
/// 機密報表的保留期就形同虛設——線上清掉了，備份裡還躺著。</para>
/// </summary>
public sealed class ReportRetentionJobService : DailyJobServiceBase
{
    /// <summary>
    /// 每日觸發時刻（<b>排程時區</b>的小時數——2026-09-14 起由組態 <c>Jobs:TimeZone</c> 決定，
    /// 不再是伺服器時區，見 <c>DailyJobServiceBase</c>）；錯開既有的 02:00／03:00。
    ///
    /// <para>⚠️ 但它沒有錯開 <c>PasswordHistoryRetentionJobService</c>——那一支也取 04:00，
    /// 且註解同樣寫著「錯開 02:00 與 03:00」。兩支各自避開了前兩支，卻沒避開彼此。
    /// 由 <c>ReportRetentionJobServiceTests.觸發時刻_維持在凌晨4點</c> 鎖住現況；
    /// 要不要挪開是營運決定。</para>
    /// </summary>
    internal const int RunAtHourValue = 4;

    /// <summary>預設保留天數。報表是可重跑的衍生資料，不需要像稽核日誌留兩年。</summary>
    internal const int DefaultRetentionDays = 90;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportRetentionJobService> _logger;

    public ReportRetentionJobService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReportRetentionJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>解析有效保留天數（設定 &lt;= 0 或未設 → 預設值）。</summary>
    public int ResolveRetentionDays()
    {
        var configured = _configuration.GetValue<int?>("Reports:RetentionDays");
        return configured is > 0 ? configured.Value : DefaultRetentionDays;
    }

    protected override int RunAtHour => RunAtHourValue;
    protected override string JobName => "報表保留期清理";
    protected override ILogger JobLogger => _logger;
    protected override IConfiguration JobConfiguration => _configuration;

    /// <summary>清理是冪等刪除，補跑與定時各跑一次無害，故不覆寫 ShouldRunAsync。</summary>
    protected override async Task ExecuteJobAsync(CancellationToken ct) => await RunOnceAsync(ct);

    /// <summary>清理一輪。<c>public</c> 供測試與手動觸發直接呼叫。</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IReportFileStore>();

        var cutoff = DateTime.UtcNow.AddDays(-ResolveRetentionDays());

        var expired = await db.ReportRuns
            .Where(r => r.RunAt < cutoff)
            .ToListAsync(ct);

        if (expired.Count == 0) return 0;

        // 刪檔失敗的紀錄**必須留下來**，理由見類別說明的「先刪檔再刪紀錄」。
        // 紀錄是下一輪清理唯一知道那個檔存在的憑據——紀錄一刪，檔案就從所有清單上
        // 消失，只剩檔案系統裡一份沒有人會再碰的機密報表。
        var removable = new List<ReportRun>(expired.Count);

        foreach (var run in expired)
        {
            if (string.IsNullOrWhiteSpace(run.FilePath)) { removable.Add(run); continue; }

            try
            {
                files.Delete(run.FilePath);
                removable.Add(run);
            }
            catch (IOException ex)
            {
                // 刪不掉單一檔案不該讓整輪清理中止——其餘檔案仍應清掉，
                // 這一筆下一輪會再試（紀錄留著，所以不會變成孤兒）。
                _logger.LogWarning(ex, "報表檔刪除失敗，保留該筆紀錄待下一輪重試：{FilePath}",
                                   run.FilePath);
            }
        }

        if (removable.Count == 0)
        {
            _logger.LogWarning("報表保留期清理：{Count} 筆過期紀錄的檔案全數刪除失敗，本輪未刪除任何紀錄。",
                               expired.Count);
            return 0;
        }

        db.ReportRuns.RemoveRange(removable);
        await db.SaveChangesAsync(ct);

        if (removable.Count < expired.Count)
            _logger.LogWarning("報表保留期清理：{Failed} 筆因檔案刪除失敗而保留，將於下一輪重試。",
                               expired.Count - removable.Count);

        _logger.LogInformation("報表保留期清理完成，刪除 {Count} 筆（保留 {Days} 天）",
                               removable.Count, ResolveRetentionDays());
        return removable.Count;
    }
}
