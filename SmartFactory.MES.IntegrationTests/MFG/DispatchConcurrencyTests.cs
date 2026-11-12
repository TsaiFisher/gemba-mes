using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// Dispatch 樂觀並發守門驗證（報工 CreateReport 防並發雙重累計 QtyDone）。
    ///
    /// 背景：DispatchController.CreateReport 以追蹤實體載入 Dispatch、累加 QtyDone 後 SaveChanges，
    /// 並 catch DbUpdateConcurrencyException 回 409；整個報工並發保護仰賴 Dispatch.RowVersion
    /// （[Timestamp] byte[]）在 PostgreSQL 下確實是有效並發權杖。
    ///
    /// 疑慮：byte[] RowVersion / IsRowVersion() 是 SQL Server 樣式——PostgreSQL 無自動遞增的
    /// rowversion 欄位，Npgsql 不會替 bytea 產值（INSERT 後恆為 NULL、UPDATE 不變更），
    /// 並發權杖原值固定 NULL → UPDATE 生 `WHERE RowVersion IS NULL`，兩個並發都符合 →
    /// 後到者亦成功（last-write-wins）→ 報工並發保護形同虛設。
    ///
    /// 本測試以兩個 DbContext 載同一筆 Dispatch 模擬並發：先存者成功，後存者「應」拋並發例外。
    /// 真 PG 才能驗（InMemory 不強制並發權杖），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class DispatchConcurrencyTests
    {
        private const string DispatchId = "DCONCUR-D";
        private const string WoId       = "DCONCUR-WO";
        private const string BatchId    = "DCONCUR-B";
        private const string Plant      = "P1";
        private const string RoleId     = "1";

        private readonly TestWebApplicationFactory _factory;

        public DispatchConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task Dispatch_ConcurrentReport_SecondSaveThrowsConcurrency()
        {
            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 兩個 context 各自載入同一筆派工（持相同並發快照）
            var dA = await dbA.Dispatches.IgnoreQueryFilters().FirstAsync(d => d.DispatchId == DispatchId);
            var dB = await dbB.Dispatches.IgnoreQueryFilters().FirstAsync(d => d.DispatchId == DispatchId);

            // A 先報工 +3 並提交
            dA.QtyDone += 3m;
            dA.UpdatedAt = DateTime.UtcNow;
            await dbA.SaveChangesAsync();

            // B 持舊快照報工 +5 → 若 RowVersion 為有效並發權杖，WHERE 比對 0 列 → 樂觀並發例外。
            // 若 byte[] RowVersion 在 Npgsql 形同無作用，B 會靜默成功（last-write-wins），覆蓋 A 的累計。
            dB.QtyDone += 5m;
            dB.UpdatedAt = DateTime.UtcNow;
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
                    BatchId = BatchId, MrpRunId = "DCONCUR-MRP", MrpRunLineSeq = 1,
                    ProductCode = "DCONCUR-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = BatchId, ProductCode = "DCONCUR-P",
                    RoutingId = "DCONCUR-R", Quantity = 10, Status = MfgM.WorkOrderStatus.InProgress,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == WoId && o.OperationSeq == 10))
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = WoId, OperationSeq = 10, WorkCenterId = "DCONCUR-WC",
                    UnitTime = 1, BatchQty = 1, OperationName = "DCONCUR OP",
                });
            db.SaveChanges();

            // 每次重建派工，確保 QtyDone 由 0 起算（可重跑）
            var stale = db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId == DispatchId).ToList();
            if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }

            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = DispatchId, WorkOrderId = WoId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0, Status = MfgM.DispatchStatus.InProgress,
                MachineCode = "DCONCUR-M", ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }
    }
}
