using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 自動重排排程（mfg-domain.md §10 自動重排）。三種時機由廠別參數各自開關（<see cref="AutoScheduleSettings"/>），
    /// 一律是全量重排——凍結範圍與手動相同（準備中、準備完成、待首件、生產中、暫停不動），
    /// 另可設凍結時界（時界內開工的已排派工也不動）；排序規則依廠別參數（未設定＝Priority）。
    /// </summary>
    public interface IAutoScheduleService
    {
        /// <summary>
        /// 派工前：本廠開啟「派工前自動重排」且排程已過時 → 先重排。給讀取畫面在查詢前呼叫，
        /// <b>永不丟例外</b>（失敗只記錄，畫面照常顯示既有排程）。回傳是否有重排。
        /// 必須在請求一開始、尚未異動任何追蹤實體前呼叫——重排會對同一個 DbContext 存檔。
        /// </summary>
        Task<bool> EnsureFreshBeforeDispatchAsync(string? plantId, CancellationToken ct = default);

        /// <summary>
        /// 背景每分鐘呼叫一次：每日定時（時刻已過且今天還沒跑，錯過當天補跑）優先，
        /// 其次異動後（已過時且安靜 90 秒，或第一筆異動已過 10 分鐘）。回傳本次執行的觸發方式，沒執行回 null。
        /// </summary>
        Task<ScheduleTrigger?> TickAsync(string plantId, DateTime localNow, DateTime utcNow, CancellationToken ct = default);
    }

    public class AutoScheduleService : IAutoScheduleService
    {
        /// <summary>最後一筆異動後要安靜多久才重排（同一波投產、報工常是連續幾筆）。</summary>
        public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(90);

        /// <summary>持續有異動時最多等多久——不能因為現場一直在報工就永遠不排。</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

        /// <summary>17:00 生產準備提醒送出之後，自動重排若改到明天的派工就另外推播。</summary>
        public const int PreparationAlertHour = 17;

        private readonly ApplicationDbContext _db;
        private readonly ISchedulingEngine _engine;
        private readonly IPlantParamService _plantParams;
        private readonly ISystemParamService _sysParams;
        private readonly IPushNotificationService _push;
        private readonly IDateTimeService _dateTime;
        private readonly ILogger<AutoScheduleService> _logger;

        public AutoScheduleService(
            ApplicationDbContext db, ISchedulingEngine engine, IPlantParamService plantParams,
            ISystemParamService sysParams, IPushNotificationService push, IDateTimeService dateTime,
            ILogger<AutoScheduleService> logger)
        {
            _db = db;
            _engine = engine;
            _plantParams = plantParams;
            _sysParams = sysParams;
            _push = push;
            _dateTime = dateTime;
            _logger = logger;
        }

        public async Task<bool> EnsureFreshBeforeDispatchAsync(string? plantId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(plantId)) return false;
            try
            {
                var settings = await AutoScheduleSettings.LoadAsync(_plantParams, plantId, ct);
                if (!settings.BeforeDispatch) return false;

                var fresh = await ScheduleFreshness.GetAsync(_db, plantId, ct);
                if (!fresh.IsStale) return false;

                return await RunAsync(plantId, settings, ScheduleTrigger.BeforeDispatch, _dateTime.Now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 派工前重排是加分項：失敗時畫面照舊顯示既有排程，不能讓派工清單打不開
                _logger.LogWarning(ex, "廠別 {PlantId} 派工前自動重排失敗，沿用既有排程", plantId);
                _db.ChangeTracker.Clear();
                return false;
            }
        }

        public async Task<ScheduleTrigger?> TickAsync(
            string plantId, DateTime localNow, DateTime utcNow, CancellationToken ct = default)
        {
            var settings = await AutoScheduleSettings.LoadAsync(_plantParams, plantId, ct);
            if (settings.DailyTime == null && !settings.OnChange) return null;

            // ① 每日定時：時刻已過、今天還沒跑過就跑（停機錯過時刻，當天之內補跑）
            if (settings.DailyTime is { } at && TimeOnly.FromDateTime(localNow) >= at)
            {
                var today = DateOnly.FromDateTime(localNow);
                var lastDaily = await _db.ScheduleRunStates.AsNoTracking()
                    .Where(s => s.PlantId == plantId).Select(s => s.LastDailyRunDate).FirstOrDefaultAsync(ct);
                if (lastDaily != today)
                {
                    if (!await RunAsync(plantId, settings, ScheduleTrigger.Daily, localNow, ct)) return null;
                    var state = await _db.ScheduleRunStates.FirstAsync(s => s.PlantId == plantId, ct);
                    state.LastDailyRunDate = today;
                    await _db.SaveChangesAsync(ct);
                    return ScheduleTrigger.Daily;
                }
            }

            // ② 異動後：已過時且安靜夠久（或已等太久）
            if (settings.OnChange)
            {
                var fresh = await ScheduleFreshness.GetAsync(_db, plantId, ct);
                if (fresh.IsStale && IsQuietEnough(fresh, utcNow)
                    && await RunAsync(plantId, settings, ScheduleTrigger.OnChange, localNow, ct))
                    return ScheduleTrigger.OnChange;
            }

            return null;
        }

        /// <summary>
        /// 安靜判定：最後一筆異動已過 <see cref="QuietPeriod"/>，或第一筆異動已過 <see cref="MaxWait"/>。
        /// 只有日曆／停機變了（沒有異動時間）或從未排過，直接放行。
        /// </summary>
        public static bool IsQuietEnough(ScheduleFreshnessInfo fresh, DateTime utcNow) =>
            fresh.LastChangeAt == null
            || utcNow - fresh.LastChangeAt.Value >= QuietPeriod
            || (fresh.FirstChangeAt != null && utcNow - fresh.FirstChangeAt.Value >= MaxWait);

        /// <summary>
        /// 執行一次全量重排（規則與凍結時界依本廠設定）；另一次正在跑（Busy）或撞上並發異動時回 false，等下一輪。
        /// </summary>
        private async Task<bool> RunAsync(
            string plantId, AutoScheduleSettings settings, ScheduleTrigger trigger, DateTime localNow, CancellationToken ct)
        {
            var before = await TomorrowSlotsAsync(plantId, localNow, ct);

            ScheduleRunResult result;
            try
            {
                result = await _engine.RunAsync(plantId, settings.Rule, localNow, runBy: null, trigger, ct,
                    freezeWindow: settings.FreezeWindow);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 排程期間有派工被報工等同時異動：整次已回滾，下一輪再排
                _logger.LogInformation("廠別 {PlantId} 自動重排（{Trigger}）遇並發異動，下一輪重試", plantId, trigger);
                _db.ChangeTracker.Clear();
                return false;
            }
            if (result.Busy)
            {
                _logger.LogInformation("廠別 {PlantId} 自動重排（{Trigger}）：另一次排程執行中，略過", plantId, trigger);
                return false;
            }

            _logger.LogInformation("廠別 {PlantId} 自動重排（{Trigger}，{Rule}）完成：排入 {Scheduled}、未排入 {Skipped}、時界保留 {Fenced}",
                plantId, trigger, settings.Rule, result.Scheduled, result.Skipped, result.Fenced);

            if (localNow.Hour >= PreparationAlertHour)
                await NotifyTomorrowChangesAsync(plantId, before, localNow, ct);
            return true;
        }

        /// <summary>
        /// 17:00 準備提醒已送出後，自動重排若改了明天派工的機台或時段，推播給準備提醒的同一角色——
        /// 現場可能已照提醒備料、備模。推播失敗不影響排程結果。
        /// </summary>
        private async Task NotifyTomorrowChangesAsync(
            string plantId, IReadOnlyDictionary<string, (string? Machine, DateTime? Start)> before,
            DateTime localNow, CancellationToken ct)
        {
            try
            {
                var after = await TomorrowSlotsAsync(plantId, localNow, ct);
                var changed = before.Keys.Union(after.Keys)
                    .Count(id => !before.TryGetValue(id, out var b) || !after.TryGetValue(id, out var a) || b != a);
                if (changed == 0) return;

                var role = await _sysParams.GetAsync(PreparationAlertBackgroundService.AlertRoleParamKey)
                           ?? PreparationAlertBackgroundService.DefaultAlertRole;
                var tomorrow = DateOnly.FromDateTime(localNow).AddDays(1);
                await _push.SendToRoleAsync(role,
                    () => new PushMessage(
                        PzText.Get("Push_ScheduleChangedTitleFmt", plantId),
                        PzText.Get("Push_ScheduleChangedBodyFmt", tomorrow, changed)),
                    url: "/PreparationBoard");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "廠別 {PlantId} 明日派工異動推播失敗", plantId);
            }
        }

        /// <summary>明天（當地日）排定的派工 → (機台, 起點)。時刻數字即當地時間（Kind=Utc 只是標籤，見 mfg-domain.md UTC 地雷）。</summary>
        private async Task<IReadOnlyDictionary<string, (string? Machine, DateTime? Start)>> TomorrowSlotsAsync(
            string plantId, DateTime localNow, CancellationToken ct)
        {
            var tomorrow = DateOnly.FromDateTime(localNow).AddDays(1);
            var from = DateTime.SpecifyKind(tomorrow.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var to   = from.AddDays(1);
            return await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == plantId && d.ScheduledStart >= from && d.ScheduledStart < to
                         && d.Status != DispatchStatus.Cancelled)
                .ToDictionaryAsync(d => d.DispatchId, d => (d.MachineCode, d.ScheduledStart), ct);
        }
    }
}

namespace SmartFactory.MES.Modules.MFG.Services
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>讀取畫面呼叫「派工前自動重排」的捷徑。服務未註冊（單元測試的精簡容器）時直接略過。</summary>
    public static class AutoScheduleHttpExtensions
    {
        public static Task EnsureScheduleFreshAsync(this HttpContext http, string? plantId) =>
            http.RequestServices.GetService<IAutoScheduleService>()?.EnsureFreshBeforeDispatchAsync(plantId)
            ?? Task.CompletedTask;
    }
}
