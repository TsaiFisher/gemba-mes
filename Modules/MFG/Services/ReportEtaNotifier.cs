using System.Collections.Concurrent;
using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 報工後的背景 ETA 重算 + Web Push 通知（桌機/手機報工共用，scheduling-spec-review 第一階段 3）。
    /// fire-and-forget 但**自建 DI scope**——不得捕捉 request-scoped 服務
    /// （request 結束後 DbContext 已 dispose，背景工作會默默失敗）。
    /// ⚠️ 自建的 scope 沒有登入使用者，DbContext 的 CurrentPlantId／CurrentRoleId 都是 null，
    /// 全域過濾會讓查詢靜默撈到 0 筆——本檔與 EtaRecalculator 的查詢一律 IgnoreQueryFilters 並自行限定範圍。
    /// </summary>
    public interface IReportEtaNotifier
    {
        /// <summary>排入背景：ETA 增量重算 → 推送「下一張派工預計開工」通知。</summary>
        void QueueRecalculate(string dispatchId, string? machineCode, string plantId, DateTime now);
    }

    /// <summary>
    /// 報工後重算 ETA＋推播＋延遲警示（背景）。
    ///
    /// <para><b>同一張派工的請求合併處理</b>（2026-10-21 換班尖峰壓測 ShiftChangeLoadTests 抓到）：原本每一筆報工各自
    /// <c>Task.Run</c> 一次重算，而重算會寫回同一張派工（ScheduledEnd）——於是①作業員緊接著的下一個寫入（下工、下一筆報工）
    /// 撞上 xmin 並發衝突，單人也會撞；②每筆報工多佔一條資料庫連線，尖峰時連線池被背景作業吃光，前景請求 500。
    /// 現在：同一張派工只留最新一次請求；單一背景迴圈先等 <see cref="DebounceMs"/> 吸收這一波，再逐筆依序處理，
    /// 背景最多只佔一條連線。重算本來就是「以目前進度推算」，晚兩秒、只算最後一次，結果相同。</para>
    /// </summary>
    public class ReportEtaNotifier : IReportEtaNotifier
    {
        /// <summary>收到請求後等多久才開始處理（吸收同一波報工）。</summary>
        internal const int DebounceMs = 2000;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReportEtaNotifier> _logger;
        private readonly ConcurrentDictionary<string, (string? MachineCode, string PlantId, DateTime Now)> _pending = new();
        private int _draining;

        public ReportEtaNotifier(IServiceScopeFactory scopeFactory, ILogger<ReportEtaNotifier> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public void QueueRecalculate(string dispatchId, string? machineCode, string plantId, DateTime now)
        {
            _pending[dispatchId] = (machineCode, plantId, now);   // 同一張派工：最新的蓋掉舊的
            StartDrainIfIdle();
        }

        private void StartDrainIfIdle()
        {
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                _ = Task.Run(DrainAsync);
        }

        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(DebounceMs);
                    var ids = _pending.Keys.ToList();
                    if (ids.Count == 0) break;
                    foreach (var id in ids)
                        if (_pending.TryRemove(id, out var req))
                            await ProcessAsync(id, req.MachineCode, req.PlantId, req.Now);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
                // 退出迴圈與新請求進來之間的空窗：有剩就再起一輪
                if (!_pending.IsEmpty) StartDrainIfIdle();
            }
        }

        private async Task ProcessAsync(string dispatchId, string? machineCode, string plantId, DateTime now)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var eta = scope.ServiceProvider.GetRequiredService<IEtaRecalculator>();
                // now 是真 UTC（報工時刻）；ScheduledStart/End 卻是廠別當地時間貼 Utc 標籤（mfg-domain.md §UTC 地雷）。
                // 原本直接拿 UTC 去算 ETA，完工時 ScheduledEnd 被寫成早 8 小時，常比開始還早，
                // 生產效率分析的排程工時就成了負數（2026-10-27 回報）。
                var localNow = await PlantLocalNowAsync(scope.ServiceProvider, plantId, now);
                await eta.RecalculateAsync(dispatchId, plantId, localNow);
                await SendEtaPushAsync(scope.ServiceProvider, machineCode, plantId, now, localNow);
                await SendLateAlertIfNeededAsync(scope.ServiceProvider, dispatchId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 現場剛好同時寫了這張派工：不是錯誤，排回去下一輪以最新進度再算（已有更新的請求就不覆蓋）
                _pending.TryAdd(dispatchId, (machineCode, plantId, now));
                _logger.LogDebug("ETA 重算與現場寫入撞在一起，下一輪重算（Dispatch {DispatchId}）", dispatchId);
            }
            catch (Exception ex)
            {
                // 背景失敗不影響報工本體，但必須記錄（禁止空 catch）
                _logger.LogError(ex, "報工後 ETA 重算/推播背景作業失敗（Dispatch {DispatchId}）", dispatchId);
            }
        }

        private static async Task<DateTime> PlantLocalNowAsync(IServiceProvider sp, string plantId, DateTime utcNow)
        {
            // 背景 scope 沒有 HTTP 上下文，IDateTimeService 讀不到使用者時區，改以廠別時區換算
            var tzId = await sp.GetRequiredService<ApplicationDbContext>().Plants.AsNoTracking()
                .Where(p => p.Id == plantId).Select(p => p.DefaultTimeZone).FirstOrDefaultAsync();
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), QueueTimeSuggestionService.ResolveTimeZone(tzId));
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }

        private static async Task SendEtaPushAsync(
            IServiceProvider sp, string? machineCode, string plantId, DateTime now, DateTime localNow)
        {
            if (string.IsNullOrEmpty(machineCode)) return;

            var db   = sp.GetRequiredService<ApplicationDbContext>();
            var push = sp.GetRequiredService<SmartFactory.MES.Services.IPushNotificationService>();

            // 找下一張 Scheduled 派工
            var next = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == plantId && d.MachineCode == machineCode
                         && d.Status == DispatchStatus.Scheduled
                         && d.ScheduledStart.HasValue && d.ScheduledStart > localNow)
                .OrderBy(d => d.ScheduledStart)
                .FirstOrDefaultAsync();

            if (next == null) return;

            // ScheduledStart/End 與 PlannedEndDate 的數字**本來就是廠別當地時間**，
            // Kind=Utc 只是貼給 Npgsql 的標籤（mfg-domain.md §UTC 地雷）。再換算一次
            // 等於多加一個時差——而且本服務是 BackgroundService，沒有 HTTP 上下文，
            // 走 IDateTimeService 只會靜默拿到空的 IUserContext。這裡不換算才是對的。
            var eta = next.ScheduledStart!.Value.ToString("MM/dd HH:mm");

            // 找 48h 內曾在此機台上工的操作員 ID
            var recentOps = await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PlantId == plantId && s.MachineCode == machineCode
                         && s.StartAt >= now.AddHours(-48)
                         && s.OperatorId != null)
                .Select(s => s.OperatorId!)
                .Distinct()
                .ToListAsync();

            if (!recentOps.Any()) return;

            // 有 push 訂閱的操作員
            var subscribedOps = await db.PushSubscriptions.AsNoTracking()
                .Where(p => recentOps.Contains(p.UserId))
                .Select(p => p.UserId)
                .Distinct()
                .ToListAsync();

            foreach (var userId in subscribedOps)
            {
                await push.SendToUserAsync(
                    userId,
                    () => new SmartFactory.MES.Services.PushMessage(PzText.Get("Push_NextDispatchTitleFmt", machineCode),
                                          PzText.Get("Push_NextDispatchBodyFmt", next.WorkOrderId, eta)),
                    url: $"/mobile/mfg/sessions.html?dispatchId={next.DispatchId}");
            }
        }

        /// <summary>
        /// ETA 重算後若生產批由「未延遲」轉為「預計延遲」，
        /// 更新 IsPredictedLate 並推送警報給有 Schedule 讀取權限且已訂閱 Push 的使用者。
        /// 從延遲恢復時靜默更新旗標，不發推播（避免通知疲勞）。
        /// </summary>
        private static async Task SendLateAlertIfNeededAsync(
            IServiceProvider sp, string dispatchId)
        {
            var db   = sp.GetRequiredService<ApplicationDbContext>();
            var push = sp.GetRequiredService<SmartFactory.MES.Services.IPushNotificationService>();

            var dispatch = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == dispatchId);
            if (dispatch == null) return;

            var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            if (wo == null) return;

            var batch = await db.ProductionBatches.IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.BatchId == wo.ProductionBatchId);
            if (batch == null) return;

            // 此批次所有派工的最晚 ETA（不含已完工者）
            var lastEnd = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => db.WorkOrders.IgnoreQueryFilters().Any(
                                w2 => w2.WorkOrderId == d.WorkOrderId
                                   && w2.ProductionBatchId == wo.ProductionBatchId)
                         && d.ScheduledEnd.HasValue
                         && d.Status != DispatchStatus.Closed
                         && d.Status != DispatchStatus.Cancelled)
                .MaxAsync(d => (DateTime?)d.ScheduledEnd);

            if (lastEnd == null) return;

            bool isNowLate = lastEnd.Value > batch.PlannedEndDate;
            bool wasLate   = batch.IsPredictedLate;

            if (isNowLate == wasLate) return;

            batch.IsPredictedLate = isNowLate;
            await db.SaveChangesAsync();

            if (!isNowLate) return; // 恢復正常：不推播

            // 找有 Schedule 讀取權限且有 Push 訂閱的使用者
            var scheduleRoleIds = await db.MenuPermissions
                .Where(mp => mp.MenuKey == "Schedule" && mp.CanRead)
                .Select(mp => mp.RoleId)
                .Distinct()
                .ToListAsync();

            if (scheduleRoleIds.Count == 0) return;

            var scheduledUserIds = await db.UserRoles
                .Where(ur => scheduleRoleIds.Contains(ur.RoleId))
                .Select(ur => ur.UserId)
                .Distinct()
                .ToListAsync();

            var subscribedPlanners = await db.PushSubscriptions.AsNoTracking()
                .Where(ps => scheduledUserIds.Contains(ps.UserId))
                .Select(ps => ps.UserId)
                .Distinct()
                .ToListAsync();

            if (subscribedPlanners.Count == 0) return;

            // ScheduledStart/End 與 PlannedEndDate 的數字**本來就是廠別當地時間**，
            // Kind=Utc 只是貼給 Npgsql 的標籤（mfg-domain.md §UTC 地雷）。再換算一次
            // 等於多加一個時差——而且本服務是 BackgroundService，沒有 HTTP 上下文，
            // 走 IDateTimeService 只會靜默拿到空的 IUserContext。這裡不換算才是對的。
            var dlvStr  = batch.PlannedEndDate.ToString("MM/dd");
            var etaStr  = lastEnd.Value.ToString("MM/dd HH:mm");
            var daysLate = Math.Round((lastEnd.Value - batch.PlannedEndDate).TotalDays, 1);

            foreach (var userId in subscribedPlanners)
            {
                await push.SendToUserAsync(
                    userId,
                    () => new SmartFactory.MES.Services.PushMessage(PzText.Get("Push_BatchLateTitleFmt", batch.BatchId),
                                          PzText.Get("Push_BatchLateBodyFmt", dlvStr, etaStr, daysLate)),
                    url: "/MFG/Schedule");
            }
        }
    }
}
