using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// ETA 全量校準背景服務（mfg-domain.md §7.5 層級 3）。
    /// 每晚 01:00 執行全量 ETA 重算，無需在即時報工流程中同步執行。
    ///
    /// <para><b>2026-09-29 改掛 <see cref="DailyJobServiceBase"/></b>：原本自帶
    /// 「算出下一個 01:00 → <c>Task.Delay</c>」的迴圈，那個寫法有兩種無聲失效
    /// （非 24 小時開機、機器睡眠），細節見基底類別的註解。本服務是 PR #113 導入基底時
    /// 唯三沒有一併改掛的其中一支，所以先前連啟動補跑都沒有。</para>
    ///
    /// <para><b>重複執行是安全的</b>：校準本身是冪等重算；推播也不會洗通知，因為
    /// <see cref="NotifyNewLateBatchesAsync"/> 比對的 <c>beforeLate</c> / <c>afterLate</c>
    /// 是<b>同一次執行內</b>的前後快照——第一次跑完之後那些批號已經是延遲狀態，
    /// 第二次跑時它們落在 <c>beforeLate</c> 裡，不會再被當成「新翻轉」。</para>
    ///
    /// <para><b>但重複執行很貴</b>：逐廠全量重算所有工單，而 IIS 的應用程式集區回收、
    /// 改組態、部署都會觸發啟動補跑，開發機一天更是好幾次。因此覆寫
    /// <c>JobScopeFactory</c> 啟用共用的執行紀錄（<c>ScheduledJobRun</c>），
    /// 基底就會自動以「今天已成功跑過就不跑」去重——補跑於是變成免費的。</para>
    /// </summary>
    public class EtaRecalibrationBackgroundService : DailyJobServiceBase
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<EtaRecalibrationBackgroundService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;

        public EtaRecalibrationBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<EtaRecalibrationBackgroundService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        protected override int RunAtHour => 1;
        protected override string JobName => "ETA 全量校準";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>啟用執行紀錄去重——全量重算很貴，不該每次應用程式啟動都做一次。</summary>
        protected override IServiceScopeFactory? JobScopeFactory => _scopeFactory;

        protected override async Task ExecuteJobAsync(CancellationToken ct)
        {
            List<Plant> plants;
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Data.ApplicationDbContext>();
                plants = await dbContext.Plants.AsNoTracking().ToListAsync(ct);
            }

            // 逐廠各自一個 scope：一廠失敗（含交易回滾）不會把追蹤中的半套異動帶到下一廠
            foreach (var plant in plants)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var sp = scope.ServiceProvider;
                    var now = sp.GetRequiredService<IDateTimeService>().Now;

                    _logger.LogInformation("開始執行廠別 {PlantId} 的 ETA 全量校準", plant.Id);
                    var result = await sp.GetRequiredService<IEtaCalibrationService>().CalibratePlantAsync(plant.Id, now, ct);
                    _logger.LogInformation("廠別 {PlantId} 的 ETA 全量校準完成：調整 {Moved} 張派工、新轉預計延遲 {Late} 批",
                        plant.Id, result.Moved, result.NewlyLate.Count);

                    await NotifyNewLateBatchesAsync(sp.GetRequiredService<IPushNotificationService>(), plant, result.NewlyLate);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 一個廠別失敗不影響其他廠別。
                    _logger.LogError(ex, "廠別 {PlantId} 的 ETA 全量校準失敗", plant.Id);
                }
            }
        }

        // 本次校準新翻轉為預測延遲的生產批 → 推播通知生管角色
        private async Task NotifyNewLateBatchesAsync(
            IPushNotificationService push,
            Plant plant,
            IReadOnlyList<(string BatchId, DateTime PlannedEnd, DateTime Eta)> newLate)
        {
            if (newLate.Count == 0) return;

            var earliest = newLate.Min(b => b.PlannedEnd);
            PushMessage Compose() => new(
                PzText.Get("Push_LateAlertTitleFmt", plant.Id),
                newLate.Count == 1
                    // 訊息寫「預計完工」：帶校準後的 ETA，不是交期（原本誤帶交期）
                    ? PzText.Get("Push_LateAlertOneFmt", newLate[0].BatchId, newLate[0].Eta)
                    : PzText.Get("Push_LateAlertManyFmt", newLate.Count, earliest));

            try
            {
                // 推播給生管角色（MFG）
                await push.SendToRoleAsync("MFG", Compose, "/Schedule");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "廠別 {PlantId} 延遲警報推播失敗（不影響校準）", plant.Id);
            }
        }
    }
}
