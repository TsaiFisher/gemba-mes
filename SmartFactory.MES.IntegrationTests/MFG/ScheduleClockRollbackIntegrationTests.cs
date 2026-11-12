using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 系統時鐘回撥後排程不可「剛排完就過時」（2026-10-28）。
    ///
    /// <para>WSL 的系統時鐘會回撥數秒（測試日誌一輪出現 4～9 次 10:14:08 → 10:14:01 這類倒退）。
    /// 回撥前寫入的異動時間戳會比回撥後才開始的排程「現在」還晚；排程的 DataAsOf 若只取當下時間，
    /// 這些**這次明明讀得到**的資料就被當成排程之後的異動，排完立刻判定過時——
    /// 「派工前自動重排」會反覆重排、排程頁一直掛過時提示。整合測試以間歇紅燈的形式先抓到。</para>
    ///
    /// <para>用獨立廠別，未來時間戳不會影響共用 P1 的其他排程測試。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScheduleClockRollbackIntegrationTests
    {
        private const string Plant = "SCLK";
        private readonly AuthedScheduleFactory _factory;

        public ScheduleClockRollbackIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task ExistingChangeStampedAfterNow_IsIncludedInRun_ScheduleNotStale()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await PurgeAsync(db);
            try
            {
                // 模擬回撥：一筆已存在的生產批異動，時間戳比現在晚兩分鐘
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = "SCLK-PB", MrpRunId = "SCLK-MRP", MrpRunLineSeq = 1, ProductCode = "SCLK-P",
                    Quantity = 1m, PlantId = Plant, CreatedByRoleId = "1",
                    CreatedAt = DateTime.UtcNow.AddMinutes(2), UpdatedAt = DateTime.UtcNow.AddMinutes(2),
                });
                await db.SaveChangesAsync();

                var engine = scope.ServiceProvider.GetRequiredService<ISchedulingEngine>();
                await engine.RunAsync(Plant, ScheduleRule.Priority, DateTime.Today);

                var fresh = await ScheduleFreshness.GetAsync(db, Plant);
                Assert.False(fresh.IsStale, $"排程讀得到的既有異動被當成排程後的異動：first={fresh.FirstChangeAt:o}");

                // 排程之後真的有新異動，仍要判定過時（校正不能把過時偵測整個關掉）
                var batch = await db.ProductionBatches.IgnoreQueryFilters().SingleAsync(b => b.BatchId == "SCLK-PB");
                batch.UpdatedAt = DateTime.UtcNow.AddMinutes(3);
                await db.SaveChangesAsync();
                Assert.True((await ScheduleFreshness.GetAsync(db, Plant)).IsStale);
            }
            finally
            {
                await PurgeAsync(db);
            }
        }

        private static async Task PurgeAsync(ApplicationDbContext db)
        {
            await db.ScheduleRunLogs.IgnoreQueryFilters().Where(l => l.PlantId == Plant).ExecuteDeleteAsync();
            await db.ScheduleRunStates.IgnoreQueryFilters().Where(s => s.PlantId == Plant).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant).ExecuteDeleteAsync();
        }
    }
}
