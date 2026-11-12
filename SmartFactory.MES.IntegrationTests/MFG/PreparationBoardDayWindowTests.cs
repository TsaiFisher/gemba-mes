using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using Xunit;
using MfgM = SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 生產前置準備看板（<c>/api/mfg/preparation</c>）的「當天」怎麼算。2026-10-05 回報：
    /// 生產排程（甘特）上當天有派工，看板卻一直顯示「該日無已排程派工」。兩個成因：
    /// ① 只取「開始時間落在當天」——前一天開工、跨到當天還在做的派工整條不見（甘特用的是時段重疊）；
    /// ② 當天的邊界直接標成 UTC 午夜：台灣的 3/10 變成 3/10 08:00～3/11 07:59，
    ///    凌晨開工的派工掉到前一天、隔天凌晨的反而算進來。
    /// 日期刻意選在遠未來，不會撞到其他測試或排程資料（只斷言自己的派工在不在）。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PreparationBoardDayWindowTests
    {
        private const string Plant = "P1", RoleId = "1", Wo = "PREPWIN-WO", Batch = "PREPWIN-B";
        private readonly AuthedScheduleFactory _factory;

        // 台北 UTC+8，無日光節約
        private static DateTime Local(int day, int hour) => new DateTime(2098, 3, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-8);

        public PreparationBoardDayWindowTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task 當天看板含跨日延續與凌晨開工_不含隔天凌晨與已完工的跨日派工()
        {
            var resp = await _factory.CreateClient().GetAsync("/api/mfg/preparation?date=2098-03-10");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray()
                .Where(r => r.GetProperty("dispatchId").GetString()!.StartsWith("PREPWIN-"))
                .ToDictionary(r => r.GetProperty("dispatchId").GetString()!, r => r);

            Assert.True(rows.ContainsKey("PREPWIN-CARRY"), "前一天開工、跨到當天的派工要出現");
            Assert.True(rows["PREPWIN-CARRY"].GetProperty("carryOver").GetBoolean());
            Assert.True(rows.ContainsKey("PREPWIN-EARLY"), "當天凌晨 02:00（本地）開工的派工要出現");
            Assert.False(rows["PREPWIN-EARLY"].GetProperty("carryOver").GetBoolean());
            Assert.False(rows.ContainsKey("PREPWIN-NEXT"), "隔天凌晨 03:00（本地）開工的不是當天");
            Assert.False(rows.ContainsKey("PREPWIN-DONE"), "已完工的跨日派工不需要準備");
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Dispatches.RemoveRange(db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo));
            db.WorkOrderOperations.RemoveRange(db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo));
            db.WorkOrders.RemoveRange(db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo));
            db.ProductionBatches.RemoveRange(db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch));
            db.SaveChanges();

            if (!db.MenuPermissions.Any(p => p.RoleId == RoleId && p.MenuKey == "PreparationBoard"))
                db.MenuPermissions.Add(new MenuPermission { RoleId = RoleId, MenuKey = "PreparationBoard", CanRead = true });

            var now = DateTime.UtcNow;
            db.ProductionBatches.Add(new MfgM.ProductionBatch
            {
                BatchId = Batch, MrpRunId = "PREPWIN-MRP", MrpRunLineSeq = 1, ProductCode = "PREPWIN-P",
                Quantity = 10, Priority = 50, PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new MfgM.WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = "PREPWIN-P", RoutingId = "PREPWIN-R",
                Quantity = 10, Status = MfgM.WorkOrderStatus.InProgress, PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            var cases = new (string Id, int Seq, DateTime Start, DateTime End, MfgM.DispatchStatus St)[]
            {
                ("PREPWIN-CARRY", 10, Local(9, 10), Local(10, 12), MfgM.DispatchStatus.InProgress),
                ("PREPWIN-EARLY", 20, Local(10, 2),  Local(10, 3),  MfgM.DispatchStatus.Scheduled),
                ("PREPWIN-NEXT",  30, Local(11, 3),  Local(11, 4),  MfgM.DispatchStatus.Scheduled),
                ("PREPWIN-DONE",  40, Local(9, 10),  Local(10, 5),  MfgM.DispatchStatus.Closed),
            };
            foreach (var c in cases)
            {
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                { WorkOrderId = Wo, OperationSeq = c.Seq, WorkCenterId = "PREPWIN-WC", UnitTime = 1, BatchQty = 1 });
                db.Dispatches.Add(new MfgM.Dispatch
                {
                    DispatchId = c.Id, WorkOrderId = Wo, OperationSeq = c.Seq, Quantity = 10, Status = c.St,
                    MachineCode = "PREPWIN-M", ScheduledStart = c.Start, ScheduledEnd = c.End,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            db.SaveChanges();
        }
    }
}
