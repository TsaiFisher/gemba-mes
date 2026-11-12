using System.Net;
using System.Text.Json;
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
    /// 派工「暫停處理」真 PG 守門（2026-10-06）：
    ///   ① 側欄伺服器渲染（三個表單、換機候選機台）與派工清單的「暫停處理」按鈕——Razor 只在編譯期檢查，
    ///      ViewBag 少給或型別不符要到執行期才 500；
    ///   ② 換機續做走真的 /Dispatch/SwitchMachine（AntiForgery＋權限＋xmin），新欄位（IsMachineLocked、
    ///      暫停原因）確實落地到 PostgreSQL。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class DispatchHoldIntegrationTests
    {
        private const string Plant  = "P1";
        private const string RoleId = "1";
        private const string WcCode = "HOLDTEST-WC";
        private const string M1 = "HOLDTEST-M1";
        private const string M2 = "HOLDTEST-M2";
        private const string WoId = "HOLDTEST-WO";
        private const string ViewId = "HOLDTEST-D-VIEW";     // 唯讀：側欄渲染
        private const string SwitchId = "HOLDTEST-D-SWITCH"; // 換機續做，每次重建

        private readonly AuthedScheduleFactory _factory;

        public DispatchHoldIntegrationTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task HoldForm_Renders_ThreeActions_AndSwitchCandidates()
        {
            var client = _factory.CreateClient();

            var resp = await client.GetAsync($"/Dispatch/HoldForm/{ViewId}");
            var html = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, html[..Math.Min(html.Length, 400)]);

            Assert.Contains("id=\"resumeForm\"", html);      // 機台正常、原因為機台停用 → 可恢復
            Assert.Contains("id=\"switchForm\"", html);
            Assert.Contains("id=\"shortCloseForm\"", html);
            Assert.Contains($"value=\"{M2}\"", html);         // 候選機台：同廠可生產、排除原機台
            Assert.DoesNotContain($"<option value=\"{M1}\"", html);
        }

        [Fact]
        public async Task DispatchList_ShowsHandleHoldButton_ForOnHoldDispatch()
        {
            var client = _factory.CreateClient();

            var html = await client.GetStringAsync($"/Dispatch?searched=true&searchText={ViewId}");

            Assert.Contains($"/Dispatch/HoldForm/{ViewId}", html);
        }

        [Fact]
        public async Task SwitchMachine_WithOutput_PersistsLockedContinuation()
        {
            var client = _factory.CreateClient();
            var token = await HarvestAntiForgeryTokenAsync(client);

            var req = new HttpRequestMessage(HttpMethod.Post, $"/Dispatch/SwitchMachine?id={SwitchId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["newMachineCode"] = M2 }),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, body);
            using (var doc = JsonDocument.Parse(body))
                Assert.True(doc.RootElement.GetProperty("success").GetBoolean(), body);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await db.Dispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.DispatchId == SwitchId);
            var cont     = await db.Dispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.DispatchId == SwitchId + "-s1");

            Assert.Equal(MfgM.DispatchStatus.Closed, original.Status);
            Assert.Equal(4, original.Quantity);
            Assert.Null(original.HoldReason);

            Assert.Equal(M2, cont.MachineCode);
            Assert.True(cont.IsMachineLocked);
            Assert.Equal(6, cont.Quantity);
            Assert.Equal(MfgM.DispatchStatus.Pending, cont.Status);
            Assert.Equal(RoleId, cont.CreatedByRoleId);   // 沿用原派工的資料權限欄，否則清單被 DataAuth 濾掉
        }

        /// <summary>
        /// 自動重排第 1 期「派工前」整條鏈在真 PG：開派工清單 → 讀廠別參數 → 判定過時 → 在 GET 內重排。
        /// 參數用本廠覆寫並於 finally 清除，不影響同 collection 其他測試。
        /// </summary>
        [Fact]
        public async Task DispatchList_WithBeforeDispatchOn_ReschedulesStaleScheduleFirst()
        {
            using var scope = _factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var plantParams = sp.GetRequiredService<SmartFactory.MES.Modules.BAS.Services.IPlantParamService>();
            var key = SmartFactory.MES.Models.ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch;
            try
            {
                await plantParams.SetAsync(key, Plant, "true");

                // 新的待排派工（異動時間＝現在）→ 排程必然過時
                var db = sp.GetRequiredService<ApplicationDbContext>();
                db.Dispatches.Add(new MfgM.Dispatch
                {
                    DispatchId = "HOLDTEST-D-NEW", WorkOrderId = WoId, OperationSeq = 10, PlantId = Plant,
                    CreatedByRoleId = RoleId, Quantity = 1, Status = MfgM.DispatchStatus.Pending,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();

                var client = _factory.CreateClient();
                var resp = await client.GetAsync("/Dispatch?searched=true&searchText=HOLDTEST-D-NEW");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

                using var check = _factory.Services.CreateScope();
                var cdb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var state = await cdb.ScheduleRunStates.AsNoTracking().SingleAsync(s => s.PlantId == Plant);
                Assert.Equal(nameof(SmartFactory.MES.Modules.MFG.Services.ScheduleTrigger.BeforeDispatch), state.LastTrigger);
                var fresh = await SmartFactory.MES.Modules.MFG.Services.ScheduleFreshness.GetAsync(cdb, Plant);
                Assert.False(fresh.IsStale,
                    $"重排後仍判定過時：dataAsOf={state.DataAsOf:o} first={fresh.FirstChangeAt:o} calendar={fresh.CalendarChanged}");
            }
            finally
            {
                await plantParams.ClearAsync(key, Plant);
            }
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == WcCode))
            {
                db.WorkCenters.Add(new BAS.WorkCenter { WorkCenterCode = WcCode, WorkCenterName = "HOLDTEST WC", IsActive = true, PlantId = Plant });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine { WorkCenterCode = WcCode, MachineCode = M1, MachineName = "HOLDTEST 1" });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine { WorkCenterCode = WcCode, MachineCode = M2, MachineName = "HOLDTEST 2" });
            }
            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == "HOLDTEST-B"))
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = "HOLDTEST-B", MrpRunId = "HOLDTEST-MRP", MrpRunLineSeq = 1,
                    ProductCode = "HOLDTEST-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });
            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
            {
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = "HOLDTEST-B", ProductCode = "HOLDTEST-P", RoutingId = "HOLDTEST-R",
                    Quantity = 10, Status = MfgM.WorkOrderStatus.InProgress,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            foreach (var seq in new[] { 10, 20 })
            {
                if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == WoId && o.OperationSeq == seq))
                    db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                    {
                        WorkOrderId = WoId, OperationSeq = seq, WorkCenterId = WcCode,
                        UnitTime = 1, BatchQty = 1, OperationName = $"HOLDTEST OP{seq}",
                    });
            }
            if (!db.MenuPermissions.Any(p => p.RoleId == RoleId && p.MenuKey == "Dispatch"))
                db.MenuPermissions.Add(new MenuPermission
                {
                    RoleId = RoleId, MenuKey = "Dispatch", CanRead = true, CanCreate = true, CanUpdate = true, CanDelete = true,
                });

            // 本廠工作日曆：「派工前重排」會估算凍結（暫停中）派工的時段，沒有日曆就丟「無法在 90 天內完成排程」，
            // 被服務吞掉後只留 WRN、不寫 ScheduleRunState。原本靠同 collection 的 EtaCalibration／QueueTimeSuggestion
            // 先跑過才有日曆——乾淨庫上順序一換就紅（2026-10-08 CI 實測）。只補缺的日期，不動別的測試鋪的時段。
            var today = DateOnly.FromDateTime(now);
            var haveDays = db.WorkCalendarSegments.IgnoreQueryFilters()
                .Where(s => s.PlantId == Plant && s.Date >= today.AddDays(-1) && s.Date < today.AddDays(70))
                .Select(s => s.Date).Distinct().ToHashSet();
            for (var i = -1; i < 70; i++)
                if (!haveDays.Contains(today.AddDays(i)))
                    db.WorkCalendarSegments.Add(new MfgM.WorkCalendarSegment
                    {
                        PlantId = Plant, Date = today.AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440,
                    });

            // 派工每次重建（換機續做會改動它、並新增 -s1）
            db.Dispatches.RemoveRange(db.Dispatches.IgnoreQueryFilters().Where(d => d.DispatchId.StartsWith("HOLDTEST-D-")));
            db.SaveChanges();

            db.Dispatches.Add(Held(ViewId, 10, done: 0));
            db.Dispatches.Add(Held(SwitchId, 20, done: 4));
            db.SaveChanges();

            MfgM.Dispatch Held(string id, int seq, decimal done) => new()
            {
                DispatchId = id, WorkOrderId = WoId, OperationSeq = seq, PlantId = Plant, CreatedByRoleId = RoleId,
                Quantity = 10, QtyDone = done, MachineCode = M1,
                Status = MfgM.DispatchStatus.OnHold,
                HoldReason = MfgM.DispatchHoldReason.MachineDown,
                HoldFromStatus = MfgM.DispatchStatus.InProgress,
                HoldAt = now,
            };
        }

        private static async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能從 /Schedule 取得 AntiForgery token");
            return m.Groups[1].Value;
        }
    }
}
