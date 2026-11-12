using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 途程等候時間建議值在真 PG、沒有 HTTP 請求的 scope（＝02:00 背景服務的樣子）下要撈得到資料、
    /// 分組查詢要翻得成 SQL，且不改途程主檔。獨立代碼前綴，不動其他測試資料。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QueueTimeSuggestionIntegrationTests
    {
        private const string Plant = "QTSUG";
        private const string Routing = "QTSUG-R";
        private readonly AuthedScheduleFactory _factory;

        public QueueTimeSuggestionIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task Recalculate_WithoutRequestContext_WritesSuggestion_LeavesRoutingUntouched()
        {
            var utcNow = DateTime.UtcNow;
            Seed(utcNow);

            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IQueueTimeSuggestionService>().RecalculateAsync(utcNow);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var s = await db.RoutingQueueTimeSuggestions.AsNoTracking().SingleAsync(x => x.RoutingId == Routing);
                Assert.Equal(10, s.OperationSeq);
                Assert.Equal(5, s.SampleCount);
                Assert.Equal(120m, s.MedianGapMinutes);
                Assert.Equal(100m, s.SuggestedQueueMinutes);
                var op10 = await db.Set<MfgM.RoutingOperation>().AsNoTracking()
                    .SingleAsync(o => o.RoutingId == Routing && o.OperationSeq == 10);
                Assert.Equal(0m, op10.QueueTime);
            }

            // 途程畫面帶出建議值（以分鐘存、畫面依工序單位換算）。測試使用者在 P1，途程畫面才看得到
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Routings.IgnoreQueryFilters()
                    .Where(r => r.RoutingId == Routing).ExecuteUpdateAsync(u => u.SetProperty(r => r.PlantId, "P1"));
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.GetAsync($"/Routing/Details/{Routing}");
            var html = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode}: {html[..Math.Min(400, html.Length)]}");
            Assert.Contains("id=\"opQueueSuggest\"", html);
            Assert.Contains("\"10\":{\"minutes\":100", html);
        }

        private void Seed(DateTime utcNow)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.ProductionSessions.RemoveRange(db.ProductionSessions.IgnoreQueryFilters().Where(s => s.PlantId == Plant));
            db.Dispatches.RemoveRange(db.Dispatches.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            db.WorkOrderOperations.RemoveRange(db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId.StartsWith("QTSUG-")));
            db.WorkOrders.RemoveRange(db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant));
            db.ProductionBatches.RemoveRange(db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant));
            db.Routings.RemoveRange(db.Routings.IgnoreQueryFilters().Where(r => r.RoutingId == Routing));
            db.WorkCalendarSegments.RemoveRange(db.WorkCalendarSegments.IgnoreQueryFilters().Where(s => s.PlantId == Plant));
            db.SaveChanges();

            // 途程與工單同廠（參照欄位檢查會擋跨廠途程）；畫面驗證前再搬到測試使用者所在的 P1
            db.Routings.Add(new MfgM.Routing { RoutingId = Routing, PlantId = Plant, ProductCode = "QTSUG-P" });
            db.Set<MfgM.RoutingOperation>().Add(new MfgM.RoutingOperation
                { RoutingId = Routing, OperationSeq = 10, OperationName = "OP10", WorkCenterId = "QTSUG-WC", MoveTime = 20 });
            db.Set<MfgM.RoutingOperation>().Add(new MfgM.RoutingOperation
                { RoutingId = Routing, OperationSeq = 20, OperationName = "OP20", WorkCenterId = "QTSUG-WC" });
            db.ProductionBatches.Add(new MfgM.ProductionBatch
            {
                BatchId = "QTSUG-B", MrpRunId = "QTSUG-MRP", MrpRunLineSeq = 1, ProductCode = "QTSUG-P",
                Quantity = 1, Priority = 50, PlannedEndDate = utcNow, PlantId = Plant, CreatedByRoleId = "1",
            });

            // 3 天前（台北）16:00 下工 → 隔天 09:00 上工；班表 08–12、13–17 → 工作時間間隔 120 分
            var localDay = DateOnly.FromDateTime(utcNow).AddDays(-3);
            for (var d = -1; d <= 4; d++)
            {
                var date = localDay.AddDays(d);
                db.WorkCalendarSegments.Add(new MfgM.WorkCalendarSegment { PlantId = Plant, Date = date, Seq = 1, StartMinute = 480, EndMinute = 720 });
                db.WorkCalendarSegments.Add(new MfgM.WorkCalendarSegment { PlantId = Plant, Date = date, Seq = 2, StartMinute = 780, EndMinute = 1020 });
            }
            var day1 = DateTime.SpecifyKind(localDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            for (var i = 0; i < 5; i++)
            {
                var wo = $"QTSUG-WO{i}";
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = wo, ProductionBatchId = "QTSUG-B", ProductCode = "QTSUG-P", RoutingId = Routing, Quantity = 1,
                    Status = MfgM.WorkOrderStatus.Closed, PlantId = Plant, CreatedByRoleId = "1",
                    PlannedStartDate = utcNow, PlannedEndDate = utcNow,
                });
                foreach (var seq in new[] { 10, 20 })
                    db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation { WorkOrderId = wo, OperationSeq = seq, WorkCenterId = "QTSUG-WC", OperationName = "OP" });
                db.Dispatches.Add(new MfgM.Dispatch { DispatchId = $"{wo}-10", WorkOrderId = wo, OperationSeq = 10, PlantId = Plant,
                    CreatedByRoleId = "1", Quantity = 1, QtyDone = 1, Status = MfgM.DispatchStatus.Closed, MachineCode = "QTSUG-M1" });
                db.Dispatches.Add(new MfgM.Dispatch { DispatchId = $"{wo}-20", WorkOrderId = wo, OperationSeq = 20, PlantId = Plant,
                    CreatedByRoleId = "1", Quantity = 1, QtyDone = 1, Status = MfgM.DispatchStatus.Closed, MachineCode = "QTSUG-M2",
                    PrevDispatchId = $"{wo}-10" });
                db.ProductionSessions.Add(new MfgM.ProductionSession { SessionId = $"QTSUG-S{i}-10", DispatchId = $"{wo}-10", WorkOrderId = wo,
                    PlantId = Plant, Status = MfgM.ProductionSessionStatus.Closed, StartAt = day1, EndAt = day1.AddHours(8) });
                db.ProductionSessions.Add(new MfgM.ProductionSession { SessionId = $"QTSUG-S{i}-20", DispatchId = $"{wo}-20", WorkOrderId = wo,
                    PlantId = Plant, Status = MfgM.ProductionSessionStatus.Closed, StartAt = day1.AddDays(1).AddHours(1), EndAt = day1.AddDays(1).AddHours(3) });
            }
            db.SaveChanges();
        }
    }
}
