using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 跨期 WIP 人工/製費結轉（reconciliation-decisions.md D4，期初快照結轉法）端到端驗證。
    ///
    /// 場景：期間 P1=209907（已關帳，快照凍結）→ P2=209908（本期重估）。
    /// P2 工時池 = WO-A 2h + WO-B 2h = 4h；WC 當月費用 人工 400 / 製費 200
    ///   → 實際人工費率 100/h、實際製費率 50/h。
    ///
    /// | 工單 | P1 快照(材/工/費) | P2 活動 | 期望 |
    /// |------|-------------------|---------|------|
    /// | WO-A | 100/300/150 | 2h session＋完工(Preliminary 50/40) | Final 人工 300+200=500、製費 150+100=250（若無結轉=200/100，斷言有鑑別力）|
    /// | WO-B | 50/120/60   | 2h session 仍在製 | P2 快照人工 120+200=320、製費 60+100=160 |
    /// | WO-C | 30/80/40    | 無活動仍在製      | P2 快照原值結轉 80/40 |
    /// | WO-V | 20/30/10    | 本期作廢          | 稽核報 WIP_VOID_LEAKAGE 60、期初調節 |
    ///
    /// 金額恆等式（§3.10-5）端到端：期初 990−60(Void 調節)=930、發料 50、部門費用 600、
    /// 完工 150+500+250=900 → 期末應為 930+50+600−900=680 = WO-B 530 + WO-C 150 ✓。
    /// 「無前期快照→期初 0」由既有 ActualRateReestimationTests（209906 無前期）覆蓋。
    /// 工時未持久化、真實過帳僅 PG 具備，故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class WipCarryForwardTests
    {
        private const string Plant = "ZZWCF";
        private const string P1 = "209907";
        private const string P2 = "209908";
        private const string Wc = "WCF-WC1";
        private const string WoA = "WCF-WO-A";
        private const string WoB = "WCF-WO-B";
        private const string WoC = "WCF-WO-C";
        private const string WoV = "WCF-WO-V";

        private readonly TestWebApplicationFactory _factory;

        public WipCarryForwardTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task RunAsync_CarriesPriorWipLayer_AndIdentityBalances()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IActualRateReestimationService>();

            var r = await svc.RunAsync(P2, Plant);
            Assert.Equal(1, r.WorkOrdersRecosted);
            Assert.Equal(2, r.WipSnapshots);   // WO-B、WO-C（WO-A 完工、WO-V 作廢不入快照）

            using var v = _factory.Services.CreateScope();
            var db = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // WO-A 跨期完工：Final = 期初凍結層 + 本期工時×本期實際費率（無結轉時為 200/100）
            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(c => c.WorkOrderId == WoA);
            Assert.Equal(500m, cost.ActualLabor);      // 300 + 2h×100
            Assert.Equal(250m, cost.ActualOverhead);   // 150 + 2h×50
            Assert.Equal(CostStatus.Final, cost.Status);

            // WO-B 跨期在製：期初層 + 本期層
            var wipB = await db.WipSnapshots.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(w => w.PeriodId == P2 && w.WorkOrderId == WoB);
            Assert.Equal(320m, wipB.WipLabor);         // 120 + 2h×100
            Assert.Equal(160m, wipB.WipOverhead);      // 60 + 2h×50
            Assert.Equal(50m, wipB.WipMaterial);

            // WO-C 本期無活動：原值結轉
            var wipC = await db.WipSnapshots.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(w => w.PeriodId == P2 && w.WorkOrderId == WoC);
            Assert.Equal(80m, wipC.WipLabor);
            Assert.Equal(40m, wipC.WipOverhead);
            Assert.Equal(30m, wipC.WipMaterial);

            // §3.10 全套（結構性守門＋金額恆等式）端到端：僅 Void 洩漏一筆、恆等式平衡
            var audit = new DataAuditService(db);
            var failures = await audit.AuditWipSnapshotConsistencyAsync(P2, Plant);
            var leak = Assert.Single(failures);
            Assert.Equal("WIP_VOID_LEAKAGE", leak.AuditType);
            Assert.Equal(WoV, leak.DocId);
            Assert.Equal(60m, leak.DifferenceAmount);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            Purge(db);

            if (!db.DocNumberConfigs.Any(c => c.DocType == "JournalEntry"))
            {
                db.DocNumberConfigs.Add(new SmartFactory.MES.Modules.BAS.Models.DocNumberConfig
                {
                    DocType = "JournalEntry", DocTypeName = "日記帳", Prefix = "JE",
                    SeqLength = 5, IsActive = true,
                });
                db.SaveChanges();
            }

            // 昔日 CI 乾淨庫陷阱（JournalId 不含廠別、序號 per-plant 各自從 1 起 → 兩廠同產
            // JE…00001 撞 PK）已由「單號設定適用廠別」重構根治：共用預設規則的廠別共用同一條
            // 序號 scope（""），跨廠必然不重號，原本墊高 90000 的隔離 workaround 已移除。

            db.AccountingPeriods.AddRange(
                new AccountingPeriod
                {
                    PeriodId = P1, PlantId = Plant, Year = 2099, Month = 7,
                    StartDate = new DateOnly(2099, 7, 1), EndDate = new DateOnly(2099, 7, 31),
                    Status = PeriodStatus.Closed,
                },
                new AccountingPeriod
                {
                    PeriodId = P2, PlantId = Plant, Year = 2099, Month = 8,
                    StartDate = new DateOnly(2099, 8, 1), EndDate = new DateOnly(2099, 8, 31),
                    Status = PeriodStatus.Closing,
                });

            db.DepartmentActualCosts.Add(new DepartmentActualCost
            {
                PeriodId = P2, WorkCenterCode = Wc,
                ActualLaborCost = 400m, ActualOverheadCost = 200m, PlantId = Plant,
            });

            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = "WCF-PB", MrpRunId = "WCF-MRP", MrpRunLineSeq = 1,
                ProductCode = "WCF-P", Quantity = 100m, PlantId = Plant, CreatedByRoleId = "1",
            });

            // P1 凍結快照（期初層）
            SeedPriorSnapshot(db, WoA, material: 100m, labor: 300m, overhead: 150m);
            SeedPriorSnapshot(db, WoB, material: 50m,  labor: 120m, overhead: 60m);
            SeedPriorSnapshot(db, WoC, material: 30m,  labor: 80m,  overhead: 40m);
            SeedPriorSnapshot(db, WoV, material: 20m,  labor: 30m,  overhead: 10m);

            // WO-A：跨期完工（P2 2h session＋Preliminary），P2 發料 50（材料 100→150）
            SeedWo(db, WoA, WorkOrderStatus.Completed, actualMaterial: 150m);
            SeedSession(db, WoA, "WCF-D-A", "WCF-S-A",
                new DateTime(2099, 8, 10, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2099, 8, 10, 10, 0, 0, DateTimeKind.Utc));   // 2h
            db.WorkOrderActualCosts.Add(new WorkOrderActualCost
            {
                WorkOrderId = WoA,
                ActualMaterial = 150m, ActualLabor = 50m, ActualOverhead = 40m,
                StandardMaterial = 150m, StandardLabor = 50m, StandardOverhead = 40m,
                VarMaterial = 0m, VarLaborEfficiency = 0m, VarOverheadRate = 0m,
                CostDate = new DateOnly(2099, 8, 20),
                Status = CostStatus.Preliminary, PlantId = Plant,
            });
            db.StockMovements.Add(new StockMovement
            {
                TypeCode = "IO", TypeCategory = "Production", DocNo = WoA + "-BF",
                MovementDate = new DateOnly(2099, 8, 5), PlantId = Plant,
                ProductCode = "WCF-M", LocationCode = "L1",
                Qty = 5m, StockQty = 5m, Direction = -1, UnitCost = 10m,   // 發料 50
            });

            // WO-B：跨期在製（P2 2h session）。材料以「已過帳領料單＋庫存異動」表達而非直接寫
            // WorkOrder.ActualMaterialCost——後者要到完工入庫才回寫，在製工單上恆為 0；
            // 直接種值等於種了一個真實系統不會出現的狀態。發料日期落在 P1：這筆料是前期投入的
            // （前期快照已含），本期發料腿只該看到 WO-A 的 50。
            SeedWo(db, WoB, WorkOrderStatus.InProgress);
            SeedIssue(db, WoB, cost: 50m, date: new DateOnly(2099, 7, 10));
            SeedSession(db, WoB, "WCF-D-B", "WCF-S-B",
                new DateTime(2099, 8, 15, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2099, 8, 15, 11, 0, 0, DateTimeKind.Utc));   // 2h

            // WO-C：跨期在製、本期無活動
            SeedWo(db, WoC, WorkOrderStatus.InProgress);
            SeedIssue(db, WoC, cost: 30m, date: new DateOnly(2099, 7, 12));

            // WO-V：前期在製、本期作廢（恆等式期初調節＋洩漏發現）
            SeedWo(db, WoV, WorkOrderStatus.Void, actualMaterial: 20m);

            db.SaveChanges();
        }

        /// <summary>已過帳發料：一張 Posted 工單領料單＋對應生產類庫存異動（凍結成本）。</summary>
        private static void SeedIssue(ApplicationDbContext db, string woId, decimal cost, DateOnly date)
        {
            var slipId = woId + "-MI";
            db.MaterialSlips.Add(new MaterialSlip
            {
                SlipId = slipId, SlipNo = slipId, Category = MaterialSlipCategory.InternalIssue,
                Status = MaterialSlipStatus.Posted, SlipDate = date,
                SourceDocType = "WorkOrder", SourceDocId = woId, PlantId = Plant,
            });
            db.StockMovements.Add(new StockMovement
            {
                TypeCode = "IO", TypeCategory = "Production", DocNo = slipId,
                MovementDate = date, PlantId = Plant,
                ProductCode = "WCF-M", LocationCode = "L1",
                Qty = 1m, StockQty = 1m, Direction = -1, UnitCost = cost,
            });
        }

        private static void SeedPriorSnapshot(ApplicationDbContext db, string woId,
            decimal material, decimal labor, decimal overhead)
        {
            db.WipSnapshots.Add(new WipSnapshot
            {
                PeriodId = P1, WorkOrderId = woId,
                WipMaterial = material, WipLabor = labor, WipOverhead = overhead,
                QtyCompleted = 0m, QtyInProcess = 10m, PlantId = Plant,
            });
        }

        private static void SeedWo(ApplicationDbContext db, string woId, WorkOrderStatus status, decimal actualMaterial = 0m)
        {
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = woId, ProductionBatchId = "WCF-PB", ProductCode = "WCF-P", RoutingId = "WCF-RT",
                Quantity = 100m, Status = status, ActualMaterialCost = actualMaterial, PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = woId, OperationSeq = 10, WorkCenterId = Wc, UnitTime = 1m,
            });
        }

        private static void SeedSession(ApplicationDbContext db, string woId, string dispatchId, string sessionId,
            DateTime startAt, DateTime endAt)
        {
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = dispatchId, WorkOrderId = woId, OperationSeq = 10,
                Quantity = 100m, Status = DispatchStatus.Closed, PlantId = Plant,
            });
            db.ProductionSessions.Add(new ProductionSession
            {
                SessionId = sessionId, DispatchId = dispatchId, WorkOrderId = woId,
                Status = ProductionSessionStatus.Closed, StartAt = startAt, EndAt = endAt, PlantId = Plant,
            });
        }

        private static void Purge(ApplicationDbContext db)
        {
            var woIds = new[] { WoA, WoB, WoC, WoV };
            var oldJes = db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .Where(e => e.SourceDocType == "ActualRateReest" && e.SourceDocId == P2).ToList();
            if (oldJes.Count > 0) { db.JournalEntries.RemoveRange(oldJes); db.SaveChanges(); }

            db.WipSnapshots.IgnoreQueryFilters().Where(w => w.PeriodId == P1 || w.PeriodId == P2).ExecuteDelete();
            db.DepartmentActualCosts.IgnoreQueryFilters().Where(d => d.PeriodId == P2).ExecuteDelete();
            var matDocs = new[] { WoA + "-BF", WoB + "-MI", WoC + "-MI" };
            db.StockMovements.IgnoreQueryFilters().Where(m => matDocs.Contains(m.DocNo)).ExecuteDelete();
            db.MaterialSlips.IgnoreQueryFilters().Where(sl => matDocs.Contains(sl.SlipId)).ExecuteDelete();
            db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => woIds.Contains(c.WorkOrderId)).ExecuteDelete();
            db.ProductionSessions.IgnoreQueryFilters().Where(s => woIds.Contains(s.WorkOrderId)).ExecuteDelete();
            db.Dispatches.IgnoreQueryFilters().Where(d => woIds.Contains(d.WorkOrderId)).ExecuteDelete();
            db.WorkOrderOperations.IgnoreQueryFilters().Where(o => woIds.Contains(o.WorkOrderId)).ExecuteDelete();
            db.WorkOrders.IgnoreQueryFilters().Where(w => woIds.Contains(w.WorkOrderId)).ExecuteDelete();
            db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == "WCF-PB").ExecuteDelete();
            db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == P1 || p.PeriodId == P2).ExecuteDelete();
        }
    }
}
