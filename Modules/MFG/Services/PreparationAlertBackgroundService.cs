using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// T-1 生產準備提醒背景服務（mfg-domain.md §23.3）。
    /// 每日 17:00 檢查隔日排程中有模具衝突或備料風險的派工，
    /// 並透過 IPushNotificationService 推送給負責角色。
    /// 推送目標角色由系統參數 MFG_ALERT_ROLE 設定（預設 MfgSupervisor）。
    ///
    /// <para><b>2026-09-29 改掛 <see cref="DailyJobServiceBase"/></b>：原本自帶
    /// 「算出下一個 17:00 → <c>Task.Delay</c>」的迴圈，那個寫法有兩種無聲失效
    /// （非 24 小時開機、機器睡眠），細節見基底類別的註解。</para>
    ///
    /// <para><b>這一支比另外兩支晚改，因為補跑對它不是安全的。</b>它每執行一次就推播一次，
    /// 而 <c>PushNotificationService.SendToRoleAsync</c> <b>不落地已送紀錄</b>
    /// （它唯一的 <c>SaveChanges</c> 是清理失效訂閱），所以先前連事後補去重的資料都沒有。
    /// 直接加上啟動補跑會讓每次應用程式重啟都重推同一則提醒給全廠負責角色——那比
    /// 「排程沒跑」更糟，因為通知疲勞不會讓任何指標亮起來。共用的執行紀錄表
    /// （<c>ScheduledJobRun</c>）補上之後，去重才有依據，這支才跟著改掛。</para>
    /// </summary>
    public class PreparationAlertBackgroundService : DailyJobServiceBase
    {
        internal const string AlertRoleParamKey  = "MFG_ALERT_ROLE";
        internal const string DefaultAlertRole   = "MfgSupervisor";

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PreparationAlertBackgroundService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;

        public PreparationAlertBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<PreparationAlertBackgroundService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        protected override int RunAtHour => 17;
        protected override string JobName => "生產準備提醒";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>啟用執行紀錄去重。對這一支這不是最佳化，是正確性前提——見類別註解。</summary>
        protected override IServiceScopeFactory? JobScopeFactory => _scopeFactory;

        /// <summary>
        /// 除了基底的「今天已成功送過就不送」，再加一道<b>時刻</b>下限。
        ///
        /// <para>啟動補跑預設不管幾點都會跑一次，但這則提醒的內容是「<b>明天</b>的排程」，
        /// 設計上是下班前才送。早上九點開機就送出去有兩個問題：用的是半天前的排程快照，
        /// 而且去重之後會把當天唯一的一次機會用掉——真正該送的 17:00 那次反而不跑了。
        /// 所以補跑只補「今天 17:00 已經過了但還沒送」的情況，不提早送。</para>
        ///
        /// <para>代價說清楚：機器整天沒開到 17:00 之後，這則提醒就當天不會送。那是對的——
        /// 隔天早上才送「明日準備提醒」，內容講的已經是當天，沒有意義。</para>
        /// </summary>
        protected override async Task<bool> ShouldRunAsync(DateTimeOffset now, CancellationToken ct)
        {
            if (now.Hour < RunAtHour)
            {
                JobLogger.LogDebug("{JobName}：現在 {Hour}:00 還不到 {RunAt}:00，不提早送",
                    JobName, now.Hour, RunAtHour);
                return false;
            }

            return await base.ShouldRunAsync(now, ct);
        }

        protected override async Task ExecuteJobAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            await RunAlertAsync(scope.ServiceProvider, ct);
        }

        /// <summary>廠別預設時區；沒設或解不開時退回 <see cref="DateTimeService.FallbackTimeZoneId"/> 並留 log（同 DateTimeService）。</summary>
        private TimeZoneInfo ResolvePlantTimeZone(SmartFactory.MES.Models.Plant plant)
        {
            var id = string.IsNullOrWhiteSpace(plant.DefaultTimeZone) ? DateTimeService.FallbackTimeZoneId : plant.DefaultTimeZone;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex, "廠別 {PlantId} 的時區代碼「{TimeZoneId}」無法解析，準備提醒改用 {Fallback}",
                    plant.Id, id, DateTimeService.FallbackTimeZoneId);
                return TimeZoneInfo.FindSystemTimeZoneById(DateTimeService.FallbackTimeZoneId);
            }
        }

        /// <summary>
        /// 廠別當地「隔日」與其 UTC 區間 [起, 迄)（ScheduledStart 是 timestamptz）。
        /// 午夜實施日光節約的時區當天 00:00 不存在，改從 01:00 起算（同 RelativePeriodResolver.LocalDayStartUtc）。
        /// </summary>
        internal static (DateOnly Tomorrow, DateTime StartUtc, DateTime EndUtc) TomorrowWindowUtc(DateTime utcNow, TimeZoneInfo tz)
        {
            var today    = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz));
            var tomorrow = today.AddDays(1);
            return (tomorrow, StartUtc(tomorrow), StartUtc(tomorrow.AddDays(1)));

            DateTime StartUtc(DateOnly day)
            {
                var local = day.ToDateTime(TimeOnly.MinValue);
                if (tz.IsInvalidTime(local)) local = local.AddHours(1);
                return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, tz), DateTimeKind.Utc);
            }
        }

        private async Task RunAlertAsync(IServiceProvider sp, CancellationToken ct)
        {
            var db          = sp.GetRequiredService<ApplicationDbContext>();
            var push        = sp.GetRequiredService<IPushNotificationService>();
            var sysParam    = sp.GetRequiredService<ISystemParamService>();

            var alertRole = await sysParam.GetAsync(AlertRoleParamKey) ?? DefaultAlertRole;

            var plants = await db.Plants.AsNoTracking().ToListAsync(ct);
            foreach (var plant in plants)
            {
                try
                {
                    // 開啟「派工前自動重排」的廠：提醒送出前，排程若已過時先重排，推出去的才是最新的明日計畫
                    await sp.GetRequiredService<IAutoScheduleService>().EnsureFreshBeforeDispatchAsync(plant.Id, ct);

                    // 「隔日」是廠別當地的隔日（同 ScheduleController.LocalDayUtc）。原本把 plant.Id 當時區代碼傳給
                    // GetLocalDate（一律解不開、退回台北），又把當地日期直接標成 UTC 午夜：台灣廠的「明天」
                    // 變成明天 08:00～後天 07:59，凌晨開工的派工漏掉、後天凌晨的反被算進來。
                    var (tomorrow, dayStartUtc, dayEndUtc) = TomorrowWindowUtc(DateTime.UtcNow, ResolvePlantTimeZone(plant));

                    // 查隔日衝突/待料派工
                    // IgnoreQueryFilters：背景服務沒有登入使用者，全域過濾是 PlantId == null，
                    // 少了這行永遠撈到 0 筆、提醒從來不會送出（已以明確 PlantId 過濾）
                    var dispatches = await db.Dispatches
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(d => d.PlantId == plant.Id
                                 && d.ScheduledStart != null
                                 && d.ScheduledStart >= dayStartUtc
                                 && d.ScheduledStart < dayEndUtc)
                        .Include(d => d.Operation)
                        .ToListAsync(ct);

                    if (!dispatches.Any()) continue;

                    // 模具衝突偵測（同 ScheduleController.DetectToolingConflicts）
                    var conflictTools = new HashSet<string>();
                    var withTool = dispatches.Where(d => d.ToolingCode != null && d.ScheduledStart.HasValue && d.ScheduledEnd.HasValue).ToList();
                    foreach (var grp in withTool.GroupBy(d => d.ToolingCode!))
                    {
                        var items = grp.OrderBy(d => d.ScheduledStart).ToList();
                        for (int i = 0; i < items.Count; i++)
                            for (int j = i + 1; j < items.Count; j++)
                            {
                                var a = items[i]; var b = items[j];
                                if (a.MachineCode == b.MachineCode) continue;
                                if (a.ScheduledStart < b.ScheduledEnd && b.ScheduledStart < a.ScheduledEnd)
                                    conflictTools.Add(grp.Key);
                            }
                    }

                    int conflictCount = dispatches.Count(d => d.ToolingCode != null && conflictTools.Contains(d.ToolingCode));
                    int matRiskCount  = dispatches.Count(d => d.MaterialRisk);

                    if (conflictCount == 0 && matRiskCount == 0)
                    {
                        _logger.LogInformation("廠別 {PlantId} 隔日（{Date}）無異常，跳過推送", plant.Id, tomorrow);
                        continue;
                    }

                    PushMessage Compose()
                    {
                        var parts = new List<string>();
                        if (conflictCount > 0) parts.Add(PzText.Get("Push_PrepToolConflictFmt", conflictCount));
                        if (matRiskCount  > 0) parts.Add(PzText.Get("Push_PrepMatRiskFmt", matRiskCount));
                        return new(PzText.Get("Push_PrepTitleFmt", plant.Id),
                            PzText.Get("Push_PrepBodyFmt", tomorrow, string.Join(PzText.Get("Common_ListSeparator"), parts)));
                    }

                    await push.SendToRoleAsync(alertRole, Compose, url: "/PreparationBoard");

                    _logger.LogInformation("廠別 {PlantId} 已推送準備提醒 → 角色 {Role}：模具衝突 {Conflict}、待料 {MatRisk}",
                        plant.Id, alertRole, conflictCount, matRiskCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "廠別 {PlantId} 準備提醒推送失敗", plant.Id);
                }
            }
        }
    }
}
