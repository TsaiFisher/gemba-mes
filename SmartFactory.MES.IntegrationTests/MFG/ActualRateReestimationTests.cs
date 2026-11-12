using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 工單實際費率期末重估（roadmap-fin-cst-sub.md §3.3 / §3.6）端到端驗證。
    ///
    /// 場景：期間 209906（專屬隔離），工作中心 WC1 當月實際費用 = 人工 1200 / 製費 600；
    /// 當月工時池 = WO1(完工,4h) + WO2(在製,2h) = 6h。
    ///   → 實際人工費率 1200/6 = 200/h；實際製費率 600/6 = 100/h。
    /// 期望：
    ///   - WO1（完工，WorkOrderActualCost Preliminary）回刷：人工 100→800(=4×200)、製費 80→400(=4×100)、翻 Final。
    ///   - WO2（在製）產 WipSnapshot：WipLabor 400(=2×200)、WipOverhead 200(=2×100)、WipMaterial 200(已實現)。
    ///   - 調整分錄（Adjustment 型）平衡：5110 貸 700、5120 貸 320、5130 借 1020。
    ///
    /// 鑑別力：若引擎未回刷，WO1.ActualLabor 仍為標準基礎的 100（≠ 斷言的 800），測試會失敗。
    /// 工時未持久化、xmin/真實過帳僅 PG 具備，故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class ActualRateReestimationTests
    {
        // 專屬廠別 + 專屬期間（2099-06）徹底隔離共用測試 DB 的他測殘留
        private const string Plant = "ZZARR";
        private const string Period = "209906";
        private const string Wc = "ARR-WC1";
        private const string Wo1 = "ARR-WO1";   // 完工
        private const string Wo2 = "ARR-WO2";   // 在製

        private readonly TestWebApplicationFactory _factory;

        public ActualRateReestimationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task RunAsync_RecostsCompletedWo_SnapshotsWip_AndPostsBalancedAdjustment()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IActualRateReestimationService>();

            var r = await svc.RunAsync(Period, Plant);

            // 摘要
            Assert.Equal(1, r.WorkOrdersRecosted);
            Assert.Equal(1, r.WipSnapshots);
            Assert.Equal(700m, r.LaborAdjustment);     // 800 - 100
            Assert.Equal(320m, r.OverheadAdjustment);  // 400 - 80
            Assert.False(string.IsNullOrEmpty(r.JournalId));

            using var v = _factory.Services.CreateScope();
            var db = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 完工工單回刷 + 翻 Final
            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(c => c.WorkOrderId == Wo1);
            Assert.Equal(800m, cost.ActualLabor);
            Assert.Equal(400m, cost.ActualOverhead);
            Assert.Equal(700m, cost.VarLaborEfficiency);
            Assert.Equal(320m, cost.VarOverheadRate);
            Assert.Equal(CostStatus.Final, cost.Status);

            // 在製品 WIP 快照（以實際費率重估）
            var wip = await db.WipSnapshots.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(w => w.PeriodId == Period && w.WorkOrderId == Wo2);
            Assert.Equal(400m, wip.WipLabor);
            Assert.Equal(200m, wip.WipOverhead);
            Assert.Equal(200m, wip.WipMaterial);
            Assert.Equal(30m, wip.QtyCompleted);
            Assert.Equal(70m, wip.QtyInProcess);

            // 調整分錄借貸平衡 + 製造成本差異科目金額
            var je = await db.JournalEntries.IgnoreQueryFilters()
                .Include(e => e.Lines).AsNoTracking().FirstAsync(e => e.JournalId == r.JournalId);
            Assert.Equal(JournalEntryType.Adjustment, je.Type);
            Assert.Equal(je.Lines.Sum(l => l.Debit), je.Lines.Sum(l => l.Credit));
            var variance = je.Lines.First(l => l.AccountCode == "5130");
            Assert.Equal(1020m, variance.Debit);   // 700 + 320
        }

        /// <summary>
        /// 人工驗收作業書 5-8：期末實際費率重估後，工單成本與傳票必須同步更新。
        ///
        /// 月結清單的步驟 4「計算工單實際成本」按下「完成」時，
        /// <c>MonthEndService.CompleteStepAsync</c> 呼叫的是
        /// <c>IWorkOrderCostFinalizationService.FinalizeAsync</c>——那支**只把
        /// Preliminary 翻成 Final，不重估、不產生任何分錄**。
        ///
        /// 而費率重估 <c>RunAsync</c> 原本只撈 <c>Status == Preliminary</c>。
        /// 兩件事都自稱是「步驟 4」（見各自的註解），順序卻是致命的：
        /// 先按完成 → 成本全被鎖成 Final → 之後再跑重估，一筆都撈不到，
        /// 回傳 0 筆、不產生調整分錄、**完全沒有任何訊號**。
        /// 結果是工單成本永遠停在標準費率算出來的值，實際費率的差額從未入帳。
        ///
        /// 而且這個順序不是「使用者可能弄錯」——月結畫面上步驟 4
        /// **根本沒有執行重估的按鈕**（只有步驟 6 匯率重評與步驟 7 NRV 有），
        /// 操作員能按的只有「完成」。所以正常操作路徑下重估必然不會發生。
        ///
        /// 修法是讓重估不再依賴 Status：以「本期的成本記錄」為範圍重算。
        /// 這是冪等的——差額取 <c>新值 − 現值</c>，已經重估過的第二次就是 0，
        /// 不會重複回刷也不會重複產生分錄。
        /// </summary>
        [Fact]
        public async Task Reestimation_StillRecosts_WhenCostsWereAlreadyLockedByMonthEndStep4()
        {
            using (var pre = _factory.Services.CreateScope())
            {
                // 月結步驟 4 的「完成」：只鎖定，不重估
                var locked = await pre.ServiceProvider
                    .GetRequiredService<IWorkOrderCostFinalizationService>()
                    .FinalizeAsync(Period, Plant);
                Assert.Equal(1, locked);
            }

            using var scope = _factory.Services.CreateScope();
            var r = await scope.ServiceProvider
                .GetRequiredService<IActualRateReestimationService>()
                .RunAsync(Period, Plant);

            Assert.Equal(1, r.WorkOrdersRecosted);
            Assert.Equal(700m, r.LaborAdjustment);
            Assert.Equal(320m, r.OverheadAdjustment);
            Assert.False(string.IsNullOrEmpty(r.JournalId),
                "成本被鎖定之後重估就不產生調整分錄——實際費率的差額從此不會入帳");

            using var v = _factory.Services.CreateScope();
            var db = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(c => c.WorkOrderId == Wo1);
            Assert.Equal(800m, cost.ActualLabor);
            Assert.Equal(400m, cost.ActualOverhead);
        }

        /// <summary>
        /// 重估必須冪等：連跑兩次的結果要跟跑一次一樣。
        /// 放寬篩選條件（不再只認 Preliminary）之後這件事才是真的需要被釘住的——
        /// 第二次撈得到同一批記錄，若差額不是取「新值 − 現值」就會重複回刷。
        /// </summary>
        /// <summary>
        /// 有工時卻沒輸入部門費用：原本費率算成 0，回刷把該中心的人工/製費整筆沖掉、調整分錄照開，
        /// 沒有任何訊號。改成擋下並列出工作中心，而且什麼都不寫。
        /// </summary>
        [Fact]
        public async Task Reestimation_Refuses_WhenWorkCenterHasHoursButNoDeptCost()
        {
            using (var del = _factory.Services.CreateScope())
                await del.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .DepartmentActualCosts.IgnoreQueryFilters()
                    .Where(d => d.PeriodId == Period && d.PlantId == Plant).ExecuteDeleteAsync();

            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<IActualRateReestimationService>()
                .RunAsync(Period, Plant));
            Assert.Contains(Wc, ex.Message);

            using var v = _factory.Services.CreateScope();
            var db = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(c => c.WorkOrderId == Wo1);
            Assert.Equal(CostStatus.Preliminary, cost.Status);
            Assert.Equal(0, await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(e => e.PlantId == Plant && e.SourceDocType == "ActualRateReest"));
        }

        [Fact]
        public async Task Reestimation_RunTwice_ProducesNoSecondAdjustment()
        {
            using (var first = _factory.Services.CreateScope())
                await first.ServiceProvider
                    .GetRequiredService<IActualRateReestimationService>()
                    .RunAsync(Period, Plant);

            using var scope = _factory.Services.CreateScope();
            var second = await scope.ServiceProvider
                .GetRequiredService<IActualRateReestimationService>()
                .RunAsync(Period, Plant);

            Assert.Equal(0m, second.LaborAdjustment);
            Assert.Equal(0m, second.OverheadAdjustment);
            Assert.True(string.IsNullOrEmpty(second.JournalId),
                "第二次重估不該再產生調整分錄——差額已經是 0");

            using var v = _factory.Services.CreateScope();
            var db = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                .AsNoTracking().FirstAsync(c => c.WorkOrderId == Wo1);
            Assert.Equal(800m, cost.ActualLabor);
            Assert.Equal(400m, cost.ActualOverhead);

            var entries = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(e => e.PlantId == Plant && e.SourceDocType == "ActualRateReest");
            Assert.Equal(1, entries);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 可重跑：清掉本測試資料
            Purge(db);

            // 調整分錄單號設定（測試 DB 未必種此 DocType）
            if (!db.DocNumberConfigs.Any(c => c.DocType == "JournalEntry"))
                db.DocNumberConfigs.Add(new DocNumberConfig
                {
                    DocType = "JournalEntry", DocTypeName = "日記帳", Prefix = "JE",
                    SeqLength = 5, IsActive = true,
                });

            db.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId = Period, PlantId = Plant, Year = 2099, Month = 6,
                StartDate = new DateOnly(2099, 6, 1), EndDate = new DateOnly(2099, 6, 30),
                Status = PeriodStatus.Open,
            });

            db.DepartmentActualCosts.Add(new DepartmentActualCost
            {
                PeriodId = Period, WorkCenterCode = Wc,
                ActualLaborCost = 1200m, ActualOverheadCost = 600m, PlantId = Plant,
            });

            // WorkOrder→ProductionBatch FK 父
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = "ARR-PB", MrpRunId = "ARR-MRP", MrpRunLineSeq = 1,
                ProductCode = "ARR-P", Quantity = 100m, PlantId = Plant, CreatedByRoleId = "1",
            });

            // ── WO1：完工（4h session）+ WorkOrderActualCost Preliminary ──
            SeedWo(db, Wo1, WorkOrderStatus.Completed);
            SeedSession(db, Wo1, "ARR-D1", "ARR-S1",
                new DateTime(2099, 6, 10, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2099, 6, 10, 12, 0, 0, DateTimeKind.Utc));   // 4h
            db.WorkOrderActualCosts.Add(new WorkOrderActualCost
            {
                WorkOrderId = Wo1,
                ActualMaterial = 500m, ActualLabor = 100m, ActualOverhead = 80m,
                StandardMaterial = 500m, StandardLabor = 100m, StandardOverhead = 80m,
                VarMaterial = 0m, VarLaborEfficiency = 0m, VarOverheadRate = 0m,
                CostDate = new DateOnly(2099, 6, 10),
                Status = CostStatus.Preliminary, PlantId = Plant,
            });

            // ── WO2：在製（2h session）+ 報工 30 ──
            // 材料以「已過帳的領料單＋庫存異動」表達，不直接寫 WorkOrder.ActualMaterialCost：
            // 那個欄位要到完工入庫才回寫，在製工單上恆為 0，直接種值等於種了一個
            // 真實系統不會出現的狀態（WIP 快照的材料腿因此驗不到東西）。
            SeedWo(db, Wo2, WorkOrderStatus.InProgress);
            SeedIssue(db, Wo2, cost: 200m, date: new DateOnly(2099, 6, 12));
            SeedSession(db, Wo2, "ARR-D2", "ARR-S2",
                new DateTime(2099, 6, 15, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2099, 6, 15, 11, 0, 0, DateTimeKind.Utc));   // 2h
            db.ProductionReports.Add(new ProductionReport
            {
                ReportId = "ARR-R2", SessionId = "ARR-S2", DispatchId = "ARR-D2", WorkOrderId = Wo2,
                Status = ProductionReportStatus.Reported, QtyOk = 30m, QtyNg = 0m,
                ReportedAt = new DateTime(2099, 6, 15, 11, 0, 0, DateTimeKind.Utc), PlantId = Plant,
            });

            db.SaveChanges();
        }

        private static void SeedWo(ApplicationDbContext db, string woId, WorkOrderStatus status, decimal actualMaterial = 0m)
        {
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = woId, ProductionBatchId = "ARR-PB", ProductCode = "ARR-P", RoutingId = "ARR-RT",
                Quantity = 100m, Status = status, ActualMaterialCost = actualMaterial, PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = woId, OperationSeq = 10, WorkCenterId = Wc, UnitTime = 1m,
            });
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
                ProductCode = "ARR-M", LocationCode = "L1",
                Qty = 1m, StockQty = 1m, Direction = -1, UnitCost = cost,
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
            // 本測試上次產生的調整分錄（連明細，走級聯）— 避免重跑時 JournalId 撞 PK
            var oldJes = db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .Where(e => e.SourceDocType == "ActualRateReest" && e.SourceDocId == Period).ToList();
            if (oldJes.Count > 0) { db.JournalEntries.RemoveRange(oldJes); db.SaveChanges(); }

            var slipIds = new[] { Wo1 + "-MI", Wo2 + "-MI" };
            db.StockMovements.IgnoreQueryFilters().Where(m => slipIds.Contains(m.DocNo)).ExecuteDelete();
            db.MaterialSlips.IgnoreQueryFilters().Where(sl => slipIds.Contains(sl.SlipId)).ExecuteDelete();
            db.WipSnapshots.IgnoreQueryFilters().Where(w => w.PeriodId == Period).ExecuteDelete();
            db.DepartmentActualCosts.IgnoreQueryFilters().Where(d => d.PeriodId == Period).ExecuteDelete();
            db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.WorkOrderId == Wo1 || c.WorkOrderId == Wo2).ExecuteDelete();
            db.ProductionReports.IgnoreQueryFilters().Where(r => r.WorkOrderId == Wo1 || r.WorkOrderId == Wo2).ExecuteDelete();
            db.ProductionSessions.IgnoreQueryFilters().Where(s => s.WorkOrderId == Wo1 || s.WorkOrderId == Wo2).ExecuteDelete();
            db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo1 || d.WorkOrderId == Wo2).ExecuteDelete();
            db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo1 || o.WorkOrderId == Wo2).ExecuteDelete();
            db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo1 || w.WorkOrderId == Wo2).ExecuteDelete();
            db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == "ARR-PB").ExecuteDelete();
            // 舊調整分錄不必清（測試以本次新 JournalId 斷言，且 JE→Line FK 不宜 ExecuteDelete 直刪表頭）
            db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == Period).ExecuteDelete();
        }
    }
}
