using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using BAS = SmartFactory.MES.Modules.BAS.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 全量排程對「凍結派工」（生產中家族）的計畫時段寫回守門。
    ///
    /// 背景缺陷（實際案例 PB-2607-00014）：凍結派工的完工依未完成量自 today 重估，但起點是用
    /// <c>ScheduledStart ??= today</c> 寫回——**已有值就不動**。於是甘特長條變成
    /// 「左緣是前一次排程留下的化石、右緣是本次自 today 的重估」，畫面上表現為
    /// 「排程開始時間在過去」，且長條長度不等於任何一次排程算出的工時。
    ///
    /// 純函式層由 <c>SchedulingEngineTests.FrozenEstimate_*</c> 釘住重估邏輯本身；
    /// 缺陷實際發生在**寫回**（RunAsync 有兩處 <c>??=</c>），純函式測試蓋不到，故本測試走真 PG +
    /// 真 /Schedule/Run，守住「跑完排程後 DB 裡的起點不再是化石」。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScheduleRunFrozenDispatchIntegrationTests
    {
        private const string Plant  = "P1";
        private const string RoleId = "1";
        private const string WcCode = "FROZENTEST-WC";
        private const string Machine = "FROZENTEST-M1";
        private const string BatchId = "FROZENTEST-B";
        private const string WoId    = "FROZENTEST-WO";
        private const string DispatchId = "FROZENTEST-D-A-PARENT";

        // 母件↔元件：WoId 為母件（無 Parent），WoChild 掛在它底下。刻意用**不同工作中心/機台**，
        // 才能把「BOM 先後」和「同機台排隊」兩件事分開驗——同機台的話光靠排隊也會過。
        // ⚠️ 派工編號刻意讓**母件排在字母序前面**：排序的最後一層 tie-break 是 DispatchId，
        //    若元件的編號剛好較小，就算 BOM 深度整個失效、光靠同批接力也會得到正確答案，
        //    測試會**因為巧合而通過**（第一版就是這樣，突變驗證才抓出來）。
        //    現在的編號讓「沒有 BOM 深度」＝「母件先排」＝母件比元件早完工＝測試轉紅。
        private const string WcCodeChild  = "FROZENTEST-WC2";
        private const string MachineChild = "FROZENTEST-M2";
        private const string WoChild      = "FROZENTEST-WO-C";
        private const string DispatchChild = "FROZENTEST-D-B-CHILD";

        /// <summary>化石起點：遠早於 today，且刻意不落在任何可用時段上。</summary>
        private static readonly DateTime StaleStart = DateTime.UtcNow.Date.AddDays(-30).AddHours(3);

        private readonly AuthedScheduleFactory _factory;

        public ScheduleRunFrozenDispatchIntegrationTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task ScheduleRun_FrozenNotStartedDispatch_RewritesStaleStartInsteadOfKeepingIt()
        {
            var client = _factory.CreateClient();
            var token  = await HarvestAntiForgeryTokenAsync(client);

            DateTime before;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var d = await db.Dispatches.IgnoreQueryFilters().FirstAsync(x => x.DispatchId == DispatchId);
                before = d.ScheduledStart!.Value;
                Assert.Equal(StaleStart, before);   // 前置成立：起點確實是化石
            }

            var runAt = DateTime.UtcNow;
            var req = new HttpRequestMessage(HttpMethod.Post, $"/Schedule/Run?plantId={Plant}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["rule"] = "Priority" }),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"/Schedule/Run 回應非 2xx（{(int)resp.StatusCode}）：{body[..Math.Min(body.Length, 400)]}");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var d = await db.Dispatches.IgnoreQueryFilters().FirstAsync(x => x.DispatchId == DispatchId);

                // 狀態與機台不得被排程改動（凍結語意）
                Assert.Equal(MfgM.DispatchStatus.PendingFPI, d.Status);
                Assert.Equal(Machine, d.MachineCode);

                Assert.NotNull(d.ScheduledStart);
                Assert.NotNull(d.ScheduledEnd);

                // 核心：尚無產出（QtyDone=0）的凍結派工，起點必須隨重估一起前移，不得留在過去
                Assert.True(d.ScheduledStart!.Value > before,
                    $"起點應被重算，但仍停在化石值 {d.ScheduledStart:o}");
                Assert.True(d.ScheduledStart!.Value >= runAt.Date,
                    $"起點不得早於本次排程執行日，實得 {d.ScheduledStart:o}");

                // 起訖必須成對且來自同一次計算
                Assert.True(d.ScheduledEnd!.Value > d.ScheduledStart!.Value,
                    $"完工應晚於起點，實得 {d.ScheduledStart:o} → {d.ScheduledEnd:o}");
            }
        }

        /// <summary>
        /// 凍結派工彼此之間的先後：母件的計畫完工不得早於自己元件的完工。
        ///
        /// 純函式層由 <c>SchedulingEngineTests.FrozenBatch_*</c> 釘住排序與接力本身；
        /// 這裡守的是**接得起來**——RunAsync 要真的把 BOM 母子鏈（跨批撈父工單）、
        /// opMap、machineAvail 都餵進去。少餵任何一項都不會拋錯，只會靜默退回舊行為。
        /// </summary>
        [Fact]
        public async Task ScheduleRun_FrozenParent_DoesNotFinishBeforeItsComponent()
        {
            var client = _factory.CreateClient();
            var token  = await HarvestAntiForgeryTokenAsync(client);

            var req = new HttpRequestMessage(HttpMethod.Post, $"/Schedule/Run?plantId={Plant}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["rule"] = "Priority" }),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"/Schedule/Run 回應非 2xx（{(int)resp.StatusCode}）：{body[..Math.Min(body.Length, 400)]}");

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var parent    = await db.Dispatches.IgnoreQueryFilters().FirstAsync(d => d.DispatchId == DispatchId);
            var component = await db.Dispatches.IgnoreQueryFilters().FirstAsync(d => d.DispatchId == DispatchChild);

            Assert.NotNull(parent.ScheduledEnd);
            Assert.NotNull(component.ScheduledEnd);

            Assert.True(parent.ScheduledEnd!.Value > component.ScheduledEnd!.Value,
                $"母件 {WoId} 完工 {parent.ScheduledEnd:o} 不得早於元件 {WoChild} 完工 " +
                $"{component.ScheduledEnd:o}——元件沒做完，母件不可能先完工");

            // 定量移轉：母件不必等元件整批做完——最後一個移轉批在元件完工時抵達，
            // 母件再做那一批即結束。故兩者的生產時段必須**重疊**（單純接力是錯的語意）。
            Assert.True(parent.ScheduledStart!.Value < component.ScheduledEnd!.Value,
                $"定量移轉時母件應與元件重疊，實得母件起點 {parent.ScheduledStart:o} " +
                $"不早於元件完工 {component.ScheduledEnd:o}——這是單純接力，不是重疊");
        }

        private static async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能從 /Schedule 取得 AntiForgery token");
            return m.Groups[1].Value;
        }

        /// <summary>建立或更新工序參數——存在就覆寫，確保測試改參數後一定吃得到新值。</summary>
        private static void UpsertOperation(
            ApplicationDbContext db, string woId, string wcCode, string name,
            decimal setup, decimal unit, MfgM.OperationMode mode, decimal batchQty)
        {
            var op = db.WorkOrderOperations
                .FirstOrDefault(o => o.WorkOrderId == woId && o.OperationSeq == 10);
            if (op == null)
            {
                op = new MfgM.WorkOrderOperation { WorkOrderId = woId, OperationSeq = 10 };
                db.WorkOrderOperations.Add(op);
            }
            op.WorkCenterId  = wcCode;
            op.OperationName = name;
            op.TimeUnit      = MfgM.OperationTimeUnit.Minute;
            op.SetupTimeUnit = MfgM.OperationTimeUnit.Minute;
            op.SetupTime     = setup;
            op.UnitTime      = unit;
            op.BatchMode     = mode;
            op.BatchQty      = batchQty;
            op.MoveTime      = 0;
            op.QueueTime     = 0;
        }

        // ── 種子：自帶工作中心／日曆／工單／凍結派工，不依賴既有資料 ────────────────
        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == WcCode))
            {
                db.WorkCenters.Add(new BAS.WorkCenter
                {
                    WorkCenterCode = WcCode, WorkCenterName = "FROZENTEST WC",
                    IsActive = true, PlantId = Plant,
                });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine
                { WorkCenterCode = WcCode, MachineCode = Machine, MachineName = "FROZENTEST 機台" });
            }
            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == WcCodeChild))
            {
                db.WorkCenters.Add(new BAS.WorkCenter
                {
                    WorkCenterCode = WcCodeChild, WorkCenterName = "FROZENTEST WC2",
                    IsActive = true, PlantId = Plant,
                });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine
                { WorkCenterCode = WcCodeChild, MachineCode = MachineChild, MachineName = "FROZENTEST 機台2" });
            }

            // 全廠層工作日曆：未來 60 天每日 08:00–17:00（涵蓋排程視窗，走第三層 fallback）
            for (int i = 0; i <= 60; i++)
            {
                var date = DateOnly.FromDateTime(now.Date.AddDays(i));
                if (db.Set<MfgM.WorkCalendarSegment>().IgnoreQueryFilters()
                      .Any(s => s.PlantId == Plant && s.WorkCenterId == null
                             && s.MachineCode == null && s.Date == date))
                    continue;
                db.Set<MfgM.WorkCalendarSegment>().Add(new MfgM.WorkCalendarSegment
                {
                    PlantId = Plant, WorkCenterId = null, MachineCode = null,
                    Date = date, Seq = 1, StartMinute = 480, EndMinute = 1020,
                });
            }

            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == BatchId))
            {
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = BatchId, MrpRunId = "FROZENTEST-MRP", MrpRunLineSeq = 1,
                    ProductCode = "FROZENTEST-P", Quantity = 100, Priority = 50,
                    PlannedEndDate = now.AddDays(14),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
            {
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = BatchId,
                    ProductCode = "FROZENTEST-P", RoutingId = "FROZENTEST-R", Quantity = 100,
                    Status = MfgM.WorkOrderStatus.InProgress,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(14),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            // 元件工單：ParentWorkOrderId 指向母件，工時刻意**遠長於**母件
            // （母件 40+2×100=240 分、元件 60+8×100=860 分），這正是實際案例出事的形狀：
            // 各自自 now 起算的話，母件會比自己的元件早完工。
            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoChild))
            {
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoChild, ProductionBatchId = BatchId,
                    ParentWorkOrderId = WoId,
                    ProductCode = "FROZENTEST-SP", RoutingId = "FROZENTEST-R", Quantity = 100,
                    Status = MfgM.WorkOrderStatus.InProgress,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(14),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            // ⚠️ 工序參數一律 upsert，不可用 `if (!Any) Add`：這個 collection 共用同一個測試庫，
            //    上一輪跑剩的工序列會留著，之後改了工時/移轉批量也**吃不到新值**——測試會用舊參數
            //    跑出舊答案，看起來像程式沒修好。本次就踩到：改成定量移轉 30 後仍讀到 BatchQty=1。
            UpsertOperation(db, WoChild, WcCodeChild, "FROZENTEST CHILD OP10",
                setup: 60, unit: 8,
                // 定量移轉、每 30 件一批——與實際案例 PB-2607-00014_02 相同。
                // 這是刻意的：唯有走到 §7.3 b 的倒推重疊，才驗得到 RunAsync 有沒有真的
                // 把 PrevOpContext 建起來並傳進去（整批移轉時 prevCtx 傳不傳結果都一樣）。
                mode: MfgM.OperationMode.Batch, batchQty: 30);
            UpsertOperation(db, WoId, WcCode, "FROZENTEST OP10",
                setup: 40, unit: 2,
                mode: MfgM.OperationMode.WholeOrder, batchQty: 1);

            // Schedule.CanUpdate（/Schedule/Run 的權限閘；CanUpdate 無 Admin 旁路）
            if (!db.MenuPermissions.Any(p => p.RoleId == RoleId && p.MenuKey == "Schedule"))
            {
                db.MenuPermissions.Add(new MenuPermission
                {
                    RoleId = RoleId, MenuKey = "Schedule",
                    CanRead = true, CanCreate = true, CanUpdate = true, CanDelete = true,
                });
            }

            db.SaveChanges();

            // 凍結派工：PendingFPI + QtyDone=0（＝一件都還沒做），起點為化石。每次重建以利重跑。
            var stale = db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.WorkOrderId == WoId || d.WorkOrderId == WoChild).ToList();
            if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }

            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = DispatchId, WorkOrderId = WoId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0,
                Status = MfgM.DispatchStatus.PendingFPI,
                MachineCode = Machine,
                ScheduledStart = StaleStart,
                ScheduledEnd   = StaleStart.AddHours(8),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = DispatchChild, WorkOrderId = WoChild, OperationSeq = 10,
                Quantity = 100, QtyDone = 0,
                Status = MfgM.DispatchStatus.PendingFPI,
                MachineCode = MachineChild,
                ScheduledStart = StaleStart,
                ScheduledEnd   = StaleStart.AddHours(8),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }
    }
}
