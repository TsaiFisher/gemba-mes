using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 自動重排排程的背景輪詢（每分鐘一次，逐廠呼叫 <see cref="IAutoScheduleService.TickAsync"/>）。
    ///
    /// <para>刻意用「每分鐘看一次牆上時鐘」而不是「算出下一次時刻再睡到那時」：後者在機器睡眠時不前進，
    /// 醒來後會錯過整個時刻（見 DailyJobServiceBase 的註解）。每日定時的「今天跑過沒」記在
    /// <c>ScheduleRunState.LastDailyRunDate</c>，重開機不會重跑、錯過時刻當天補跑。</para>
    ///
    /// <para>各廠各自一個 scope、各自 try/catch：一廠失敗不影響其他廠。沒有開任何自動模式的廠只讀參數就結束。</para>
    /// </summary>
    public class AutoScheduleBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AutoScheduleBackgroundService> _logger;

        public AutoScheduleBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AutoScheduleBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 啟動時先讓出一段時間給 migration 與其他啟動工作
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }

            using var timer = new PeriodicTimer(Interval);
            do
            {
                await TickAllPlantsAsync(stoppingToken);
            }
            while (await WaitNextAsync(timer, stoppingToken));
        }

        private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try { return await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { return false; }
        }

        internal async Task TickAllPlantsAsync(CancellationToken ct)
        {
            List<string> plantIds;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                plantIds = await db.Plants.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "自動重排排程：讀取廠別清單失敗");
                return;
            }

            foreach (var plantId in plantIds)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sp = scope.ServiceProvider;
                    var now = sp.GetRequiredService<IDateTimeService>().Now;
                    await sp.GetRequiredService<IAutoScheduleService>().TickAsync(plantId, now, DateTime.UtcNow, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "廠別 {PlantId} 自動重排排程失敗", plantId);
                }
            }
        }
    }
}
