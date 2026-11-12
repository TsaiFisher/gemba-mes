using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// OeeService 端到端：機台 OEE-MC 於 2099-06-01
    ///   計畫工時 = TM_WKS 8h ×60 = 480 分；稼動 = Session 08:00→14:40 = 400 分；
    ///   產出 100（良 95 / 不良 5）；標準單位工時 2 分 → 理想 = 100×2 = 200 分。
    /// 期望：可用率 400/480≈0.8333、表現性 200/400=0.5、良率 95/100=0.95、OEE≈0.3958。
    /// </summary>
    [Collection("Integration")]
    public class OeeServiceTests
    {
        private const string Plant   = "ZZOEE";
        private const string Machine = "OEE-MC";
        private const string Wc      = "OEE-WC";
        private const string Wo      = "OEE-WO";
        private const string Disp    = "OEE-D";
        private const string Session = "OEE-S";

        private readonly TestWebApplicationFactory _factory;

        public OeeServiceTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task Build_computes_oee_factors_per_machine()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IOeeService>();

            var rpt = await svc.BuildAsync(
                Plant, new DateOnly(2099, 6, 1), new DateOnly(2099, 6, 1), null);

            var row = Assert.Single(rpt.Rows);
            Assert.Equal(Machine, row.MachineCode);
            Assert.Equal(480m, row.PlannedMinutes);
            Assert.Equal(400m, row.RunMinutes);
            Assert.Equal(200m, row.IdealMinutes);
            Assert.Equal(100m, row.TotalQty);
            Assert.Equal(95m,  row.GoodQty);
            Assert.Equal(0.8333m, row.Availability);
            Assert.Equal(0.5m,    row.Performance);
            Assert.Equal(0.95m,   row.Quality);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Purge(db);

            db.WorkCenters.Add(new WorkCenter
            {
                WorkCenterCode = Wc, WorkCenterName = "OEE 工作中心", PlantId = Plant,
            });
            db.WorkCenterMachines.Add(new WorkCenterMachine
            {
                WorkCenterCode = Wc, MachineCode = Machine, MachineName = "OEE 測試機",
            });

            db.WorkCalendars.Add(new WorkCalendar
            {
                PlantId = Plant, MachineCode = Machine, WorkCenterId = Wc,
                Date = new DateOnly(2099, 6, 1), TM_WKS = 8m, TM_WKO = 0m,
            });

            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = "OEE-PB", MrpRunId = "OEE-MRP", MrpRunLineSeq = 1,
                ProductCode = "OEE-P", Quantity = 100m, PlantId = Plant, CreatedByRoleId = "1",
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = "OEE-PB", ProductCode = "OEE-P",
                RoutingId = "OEE-RT", Quantity = 100m, Status = WorkOrderStatus.InProgress, PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = Wc,
                UnitTime = 2m, TimeUnit = OperationTimeUnit.Minute,
            });

            db.Dispatches.Add(new Dispatch
            {
                DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10, MachineCode = Machine,
                Quantity = 100m, Status = DispatchStatus.Closed, PlantId = Plant,
            });
            db.ProductionSessions.Add(new ProductionSession
            {
                SessionId = Session, DispatchId = Disp, WorkOrderId = Wo, MachineCode = Machine,
                Status = ProductionSessionStatus.Closed,
                StartAt = new DateTime(2099, 6, 1, 8, 0, 0, DateTimeKind.Utc),
                EndAt   = new DateTime(2099, 6, 1, 14, 40, 0, DateTimeKind.Utc),   // 400 分
                PlantId = Plant,
            });
            db.ProductionReports.Add(new ProductionReport
            {
                ReportId = "OEE-R1", SessionId = Session, DispatchId = Disp, WorkOrderId = Wo,
                Status = ProductionReportStatus.Reported, QtyOk = 95m, QtyNg = 5m,
                ReportedAt = new DateTime(2099, 6, 1, 12, 0, 0, DateTimeKind.Utc), PlantId = Plant,
            });

            db.SaveChanges();
        }

        private static void Purge(ApplicationDbContext db)
        {
            void Del<T>(IQueryable<T> q) where T : class
            {
                var list = q.ToList();
                if (list.Count > 0) db.Set<T>().RemoveRange(list);
            }
            Del(db.ProductionReports.IgnoreQueryFilters().Where(r => r.PlantId == Plant));
            Del(db.ProductionSessions.IgnoreQueryFilters().Where(s => s.PlantId == Plant));
            Del(db.Dispatches.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            Del(db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo));
            Del(db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant));
            Del(db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant));
            Del(db.WorkCalendars.IgnoreQueryFilters().Where(c => c.PlantId == Plant));
            Del(db.WorkCenterMachines.IgnoreQueryFilters().Where(m => m.MachineCode == Machine));
            Del(db.WorkCenters.IgnoreQueryFilters().Where(w => w.WorkCenterCode == Wc));
            db.SaveChanges();
        }
    }
}
