using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 每晚 01:00 ETA 全量校準在真 PG、**沒有 HTTP 請求的 scope**（＝背景服務的樣子）下要撈得到資料並寫回。
    /// 原實作受全域過濾影響每晚撈到 0 筆。用獨立廠別代碼，不動同 collection 其他測試的資料。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class EtaCalibrationIntegrationTests
    {
        private const string Plant = "ETACAL";
        private readonly AuthedScheduleFactory _factory;

        public EtaCalibrationIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task CalibratePlant_WithoutRequestContext_ShiftsLateChain_AndFlagsBatchLate()
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(8), DateTimeKind.Utc);
            Seed(now);

            using (var scope = _factory.Services.CreateScope())   // 沒有 HttpContext：CurrentPlantId／RoleId 皆為 null
            {
                var svc = scope.ServiceProvider.GetRequiredService<IEtaCalibrationService>();
                var result = await svc.CalibratePlantAsync(Plant, now);

                Assert.True(result.Moved >= 2, $"應調整生產中與下一道派工，實得 {result.Moved}");
                Assert.Contains(result.NewlyLate, b => b.BatchId == "ETACAL-B");
            }

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var next = await db.Dispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.DispatchId == "ETACAL-D20");
                Assert.Equal(now.AddHours(10), next.ScheduledStart);
                Assert.True((await db.ProductionBatches.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(b => b.BatchId == "ETACAL-B")).IsPredictedLate);
            }
        }

        private void Seed(DateTime now)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Dispatches.RemoveRange(db.Dispatches.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            db.WorkOrderOperations.RemoveRange(db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == "ETACAL-WO"));
            db.WorkOrders.RemoveRange(db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant));
            db.ProductionBatches.RemoveRange(db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant));
            db.WorkCalendarSegments.RemoveRange(db.WorkCalendarSegments.IgnoreQueryFilters().Where(s => s.PlantId == Plant));
            db.SaveChanges();

            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == "ETACAL-WC"))
            {
                db.WorkCenters.Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenter
                    { WorkCenterCode = "ETACAL-WC", WorkCenterName = "ETACAL", IsActive = true, PlantId = Plant });
                foreach (var m in new[] { "ETACAL-M1", "ETACAL-M2" })
                    db.WorkCenterMachines.Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenterMachine
                        { WorkCenterCode = "ETACAL-WC", MachineCode = m, MachineName = m });
            }

            db.ProductionBatches.Add(new MfgM.ProductionBatch
            {
                BatchId = "ETACAL-B", MrpRunId = "ETACAL-MRP", MrpRunLineSeq = 1, ProductCode = "ETACAL-P",
                Quantity = 1, Priority = 50, PlannedEndDate = now.AddHours(4), PlantId = Plant, CreatedByRoleId = "1",
            });
            db.WorkOrders.Add(new MfgM.WorkOrder
            {
                WorkOrderId = "ETACAL-WO", ProductionBatchId = "ETACAL-B", ProductCode = "ETACAL-P", RoutingId = "R",
                Quantity = 600, Status = MfgM.WorkOrderStatus.InProgress, PlantId = Plant, CreatedByRoleId = "1",
                PlannedStartDate = now, PlannedEndDate = now.AddHours(4),
            });
            foreach (var seq in new[] { 10, 20 })
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = "ETACAL-WO", OperationSeq = seq, OperationName = "OP", WorkCenterId = "ETACAL-WC",
                    TimeUnit = MfgM.OperationTimeUnit.Minute, UnitTime = 1, BatchQty = 1,
                });
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = "ETACAL-D10", WorkOrderId = "ETACAL-WO", OperationSeq = 10, PlantId = Plant, CreatedByRoleId = "1",
                MachineCode = "ETACAL-M1", Quantity = 600, Status = MfgM.DispatchStatus.InProgress,
                ScheduledStart = now.AddHours(-10), ScheduledEnd = now,
            });
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = "ETACAL-D20", WorkOrderId = "ETACAL-WO", OperationSeq = 20, PlantId = Plant, CreatedByRoleId = "1",
                MachineCode = "ETACAL-M2", Quantity = 60, Status = MfgM.DispatchStatus.Scheduled,
                ScheduledStart = now, ScheduledEnd = now.AddHours(1),
            });
            for (var i = -1; i < 70; i++)
                db.WorkCalendarSegments.Add(new MfgM.WorkCalendarSegment
                {
                    PlantId = Plant, Date = DateOnly.FromDateTime(now).AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440,
                });
            db.SaveChanges();
        }
    }
}
