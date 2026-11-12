using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 開關關閉時取代派工器註冊的一次性通知（§17.8）。
///
/// <para>關掉 <c>Features:ReportTools:Enabled</c> 的後果是「所有 <c>IsActive</c> 的排程從此不執行」，
/// 而那與「沒有任何排程到期」在外觀上完全一樣——沒有錯誤、沒有紀錄、沒有人會發現。
/// 這與 §7.8 S2「不得靜默停止」是同一條原則，只是這次的原因是組態而非權限，
/// 所以停用這件事也必須在啟動 log 上出聲，並且要講出**有幾筆**受影響。</para>
/// </summary>
public sealed class ReportSchedulingDisabledNotice : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportSchedulingDisabledNotice> _logger;

    public ReportSchedulingDisabledNotice(
        IServiceScopeFactory scopeFactory,
        ILogger<ReportSchedulingDisabledNotice> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var pending = await db.ReportDefinitions
                .CountAsync(d => d.IsActive && d.ScheduleKind != null, cancellationToken);

            _logger.LogWarning(
                "報表排程已停用（{Key}=false），{Count} 筆啟用中的排程不會執行",
                ReportToolsFeature.ConfigKey, pending);
        }
        catch (Exception ex)
        {
            // 這只是一行提示。資料庫還沒 migrate 完（乾淨部署的第一次啟動）時查詢會失敗，
            // 絕不能因此讓整個 host 起不來。
            _logger.LogWarning(ex, "報表排程已停用（{Key}=false）；查詢受影響筆數失敗",
                ReportToolsFeature.ConfigKey);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
