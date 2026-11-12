using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// WorkOrder xmin 樂觀並發守門（防完工入庫並發雙重繳庫）。
    ///
    /// 背景：ProductionCompletionService.CompleteAsync 以 AsNoTracking 讀取工單狀態（無列鎖），
    /// 兩個並發「完工入庫」請求可能都讀到 Completed → 各自過帳一次 IT 繳庫造成庫存翻倍。
    /// 修法：WorkOrder.UseXminAsConcurrencyToken()——收尾 UPDATE 帶 WHERE xmin，後到者比對失敗
    /// 拋 DbUpdateConcurrencyException → 整段交易回滾，未重複過帳；Controller 轉 409 友善訊息。
    ///
    /// 本測試以兩個 DbContext 載同一筆工單模擬並發：先存者成功（xmin 遞增），後存者必拋並發例外。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class WorkOrderConcurrencyTests
    {
        private const string WoId    = "XMINTEST-WO";
        private const string BatchId = "XMINTEST-B";
        private const string Plant   = "P1";
        private const string RoleId  = "1";

        private readonly TestWebApplicationFactory _factory;

        public WorkOrderConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task WorkOrder_ConcurrentUpdate_SecondSaveThrowsConcurrency()
        {
            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 兩個 context 各自載入同一筆工單（持相同 xmin 快照）
            var woA = await dbA.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == WoId);
            var woB = await dbB.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == WoId);

            var xminA = dbA.Entry(woA).Property("xmin").CurrentValue;
            var xminB = dbB.Entry(woB).Property("xmin").CurrentValue;
            Assert.Equal(xminA, xminB);   // 兩 context 應載到相同 xmin 快照

            // A 先更新並提交 → 該列 xmin 遞增
            woA.ActualLaborCost += 1m;
            woA.UpdatedAt = DateTime.UtcNow;
            await dbA.SaveChangesAsync();

            var xminAfter = dbA.Entry(woA).Property("xmin").CurrentValue;
            Assert.NotEqual(xminB, xminAfter);   // A 存檔後 xmin 必須遞增

            // B 持舊 xmin 更新 → WHERE xmin 比對 0 列 → 樂觀並發例外
            woB.ActualLaborCost += 2m;
            woB.UpdatedAt = DateTime.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == BatchId))
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = BatchId, MrpRunId = "XMINTEST-MRP", MrpRunLineSeq = 1,
                    ProductCode = "XMINTEST-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = BatchId, ProductCode = "XMINTEST-P",
                    RoutingId = "XMINTEST-R", Quantity = 10, Status = MfgM.WorkOrderStatus.Released,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });

            db.SaveChanges();
        }
    }
}
