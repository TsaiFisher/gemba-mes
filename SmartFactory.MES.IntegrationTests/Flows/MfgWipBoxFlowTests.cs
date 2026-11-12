using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 生產中容器（container-tracking.md §9.4 第 2 期）：報工掃箱號 → 箱子綁工單＋工序 →
    /// 下一道工序報工掃同一箱接手 → 完工入庫選這個箱子轉成在庫批號。
    ///
    /// 釘的是：
    ///   - 在製數量從報工容器紀錄推導，續裝累加、換工序重新起算（上一站的已交出去）
    ///   - 不混裝：裝著別張工單在製品或庫存批號的箱子，桌機報工整筆擋下、什麼都不寫
    ///   - 工單結案／作廢後在製綁定自動失效（判斷時才算，不靠掛勾）
    /// 報工一律走 <see cref="DispatchController"/>，箱號欄才算接上。
    /// </summary>
    [Collection("Integration")]
    public class MfgWipBoxFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZWIPBOX";

        private const string Fg      = "FLOW-WIPB-FG";
        private const string Batch   = "FLOW-WIPB-PB";
        private const string Wo      = "FLOW-WIPB-WO";
        private const string Wo2     = "FLOW-WIPB-WO2";
        private const string BoxType = "FLOW-WIPB-T";
        private const string Prefix  = "ZZWIPB";

        public MfgWipBoxFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private static string Disp(string wo, int op) => $"{wo}-D{op}";
        private static string Sess(string wo, int op) => $"{wo}-S{op}";

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "在製箱流程成品"));
            using var scope = NewScope(out var db);
            var wos = new[] { Wo, Wo2 };
            var reportIds = await db.ProductionReports.IgnoreQueryFilters()
                .Where(r => wos.Contains(r.WorkOrderId)).Select(r => r.ReportId).ToListAsync();
            await db.ProductionReportContainers.Where(c => reportIds.Contains(c.ReportId)).ExecuteDeleteAsync();
            await db.ProductionReports.IgnoreQueryFilters().Where(r => wos.Contains(r.WorkOrderId)).ExecuteDeleteAsync();
            await db.ProductionSessions.IgnoreQueryFilters().Where(s => wos.Contains(s.WorkOrderId)).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => wos.Contains(d.WorkOrderId)).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => wos.Contains(o.WorkOrderId)).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => wos.Contains(w.WorkOrderId)).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
            await db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.ContainerUnits.IgnoreQueryFilters().Where(u => u.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            if (!await db.Containers.AnyAsync(c => c.ContainerCode == BoxType))
                db.Containers.Add(new Container { ContainerCode = BoxType, ContainerName = "在製箱流程週轉箱", MaxCapacity = 50m, IsActive = true });
            await db.SaveChangesAsync();
        }

        /// <summary>生產中的工單：工序 10、20 各一張開工中的派工（各 50）與一個上工紀錄。</summary>
        private async Task SeedInProgressWorkOrderAsync(string wo)
        {
            using var scope = NewScope(out var db);
            if (!await db.ProductionBatches.IgnoreQueryFilters().AnyAsync(b => b.BatchId == Batch))
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-WIPB-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = 100m, PlantId = Plant, CreatedByRoleId = RoleId,
                });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = wo, ProductionBatchId = Batch, ProductCode = Fg, RoutingId = "FLOW-WIPB-RT",
                Quantity = 50m, Status = WorkOrderStatus.InProgress,
                ActualLaborCost = 500m, ActualOverheadCost = 500m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            foreach (var op in new[] { 10, 20 })
            {
                db.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = wo, OperationSeq = op, WorkCenterId = "FLOW-WC", UnitTime = 1m });
                db.Dispatches.Add(new Dispatch
                {
                    DispatchId = Disp(wo, op), WorkOrderId = wo, OperationSeq = op,
                    Quantity = 50m, QtyDone = 0m, Status = DispatchStatus.InProgress, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.ProductionSessions.Add(new ProductionSession
                {
                    SessionId = Sess(wo, op), DispatchId = Disp(wo, op), WorkOrderId = wo,
                    Status = ProductionSessionStatus.Open, StartAt = DateTime.UtcNow.AddHours(-1), PlantId = Plant,
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<IReadOnlyList<string>> CreateUnitsAsync(int n)
        {
            using var scope = NewScope(out _);
            return await Svc<IContainerUnitService>(scope).CreateBatchAsync(BoxType, Prefix, n, UserId);
        }

        private DispatchController NewDispatchController(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new DispatchController(
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IInventoryReservationService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IWorkOrderMaterialReadinessService>(),
                sp.GetRequiredService<IProductionCostAccrualService>(),
                sp.GetRequiredService<IStagingSourceService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<IFpiGateService>(),
                sp.GetRequiredService<IMfgSimplifiedModeService>(),
                sp.GetRequiredService<IOperationSequenceGuard>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        private async Task<(bool Ok, string? Message)> ReportAsync(string wo, int op, decimal qtyOk, string box)
        {
            using var scope = NewScope(out var db);
            var json = Assert.IsType<JsonResult>(await NewDispatchController(scope, db)
                .CreateReport(Disp(wo, op), Sess(wo, op), qtyOk, 0m, containerCode: null, containerUnitId: box));
            var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
            var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value) as string;
            return (Equals(ok, true), msg);
        }

        private async Task<(ContainerUnit Unit, decimal WipQty, bool InWip)> BoxAsync(string id)
        {
            using var scope = NewScope(out var db);
            var u   = await db.ContainerUnits.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.ContainerUnitId == id);
            var svc = Svc<IContainerUnitService>(scope);
            return (u, await svc.WipQtyAsync(u), await svc.IsInWipAsync(u));
        }

        [Fact]
        public async Task 報工裝箱_續裝累加_下一工序接手_完工入庫轉在庫()
        {
            await SeedInProgressWorkOrderAsync(Wo);
            var box = (await CreateUnitsAsync(1))[0];

            var r = await ReportAsync(Wo, 10, 30m, box);
            Assert.True(r.Ok, r.Message);
            r = await ReportAsync(Wo, 10, 20m, box.ToLowerInvariant());     // 掃描大小寫不同也認得
            Assert.True(r.Ok, r.Message);

            var (u, qty, inWip) = await BoxAsync(box);
            Assert.True(inWip);
            Assert.Equal((Wo, (int?)10, Fg, (string?)null), (u.WorkOrderId, u.OperationSeq, u.ProductCode, u.LotNo));
            Assert.Equal(50m, qty);                                          // 同工序續裝累加

            using (var scope = NewScope(out var db))
            {
                var prc = await db.ProductionReportContainers.AsNoTracking()
                    .Where(c => c.ContainerUnitId == box).ToListAsync();
                Assert.Equal(2, prc.Count);
                Assert.All(prc, c => Assert.Equal(BoxType, c.ContainerCode));   // 種類跟著箱子
            }

            // 工序 20 整箱接手：改綁到 20，數量重新起算（工序 10 的 50 已交出去，不是 100）
            r = await ReportAsync(Wo, 20, 50m, box);
            Assert.True(r.Ok, r.Message);
            (u, qty, _) = await BoxAsync(box);
            Assert.Equal((int?)20, u.OperationSeq);
            Assert.Equal(50m, qty);

            // 兩道都報滿 → 工單自動完工；完工入庫選這個在製箱 → 轉成在庫批號
            using (var scope = NewScope(out var db))
                Assert.Equal(WorkOrderStatus.Completed,
                    (await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo)).Status);

            using (var scope = NewScope(out _))
            {
                var svc = Svc<IContainerUnitService>(scope);
                Assert.Equal(new[] { box }, await svc.EmptyUnitIdsAsync(BoxType, 5, Wo));   // 帶入空箱帶得到它
                await Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(50m, BoxType, new[] { box }, UserId));
            }

            (u, qty, inWip) = await BoxAsync(box);
            Assert.False(inWip);
            Assert.Equal((null, (int?)null, $"{Wo}-01"), (u.WorkOrderId, u.OperationSeq, u.LotNo));
            using (var scope = NewScope(out _))
                Assert.Equal(50m, await Svc<IContainerUnitService>(scope).BoundQtyAsync(u));

            using (var scope = NewScope(out var db))
                Assert.Equal(new[] { ContainerUnitActions.Create, ContainerUnitActions.WipLoad, ContainerUnitActions.WipLoad,
                                     ContainerUnitActions.WipLoad, ContainerUnitActions.Bind },
                    await db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId == box)
                        .OrderBy(h => h.Id).Select(h => h.Action).ToArrayAsync());
        }

        [Fact]
        public async Task 別張工單的在製箱_報工整筆擋下_入庫也不能用()
        {
            await SeedInProgressWorkOrderAsync(Wo);
            await SeedInProgressWorkOrderAsync(Wo2);
            var box = (await CreateUnitsAsync(1))[0];
            Assert.True((await ReportAsync(Wo, 10, 30m, box)).Ok);

            var r = await ReportAsync(Wo2, 10, 10m, box);
            Assert.False(r.Ok);
            Assert.Contains(Wo, r.Message);

            using (var scope = NewScope(out var db))
            {
                // 擋下來就什麼都沒寫：派工數量沒動、沒有報工紀錄
                Assert.Equal(0m, (await db.Dispatches.IgnoreQueryFilters().SingleAsync(d => d.DispatchId == Disp(Wo2, 10))).QtyDone);
                Assert.False(await db.ProductionReports.IgnoreQueryFilters().AnyAsync(x => x.WorkOrderId == Wo2));
                var svc = Svc<IContainerUnitService>(scope);
                Assert.Empty(await svc.EmptyUnitIdsAsync(BoxType, 5, Wo2));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    svc.TakeEmptyForBindingAsync(new[] { box }, BoxType, Wo2));
            }

            // 手機報工的共用入口：同樣判定不能裝，但只回原因、箱子不動（報工照收由呼叫端決定）
            using (var scope = NewScope(out var db))
            {
                var dispatch = await db.Dispatches.IgnoreQueryFilters().SingleAsync(d => d.DispatchId == Disp(Wo2, 10));
                var (unit, error) = await ReportContainers.LoadBoxAsync(db, box, dispatch, 10m, UserId, DateTime.UtcNow);
                Assert.Null(unit);
                Assert.Contains(Wo, error);
            }
            var (u, qty, _) = await BoxAsync(box);
            Assert.Equal((Wo, (int?)10, 30m), (u.WorkOrderId, u.OperationSeq, qty));
        }

        [Fact]
        public async Task 裝著庫存批號的箱子_不能報工裝箱()
        {
            await SeedInProgressWorkOrderAsync(Wo);
            var box = (await CreateUnitsAsync(1))[0];
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-WIPB-OPEN", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "WIPB-LOT",
                    Qty = 5m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = new DateOnly(2097, 7, 1), UomCode = Uom, StockUomCode = Uom, UnitPrice = 20m,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                var u = await db.ContainerUnits.SingleAsync(x => x.ContainerUnitId == box);
                Svc<IContainerUnitService>(scope).Bind(u, Fg, "WIPB-LOT", 5m, "FLOW-WIPB-OPEN", UserId);
                await db.SaveChangesAsync();
            }

            var r = await ReportAsync(Wo, 10, 10m, box);
            Assert.False(r.Ok);
            Assert.Contains("WIPB-LOT", r.Message);
            Assert.Equal("WIPB-LOT", (await BoxAsync(box)).Unit.LotNo);
        }

        [Fact]
        public async Task 工單結案後在製箱視為空箱_手動清空也解除在製()
        {
            await SeedInProgressWorkOrderAsync(Wo);
            var boxes = await CreateUnitsAsync(2);
            Assert.True((await ReportAsync(Wo, 10, 10m, boxes[0])).Ok);
            Assert.True((await ReportAsync(Wo, 10, 10m, boxes[1])).Ok);

            // 手動清空：在製綁定解除，歷程記下當時的工單與數量
            using (var scope = NewScope(out _))
                await Svc<IContainerUnitService>(scope).ClearAsync(boxes[1], UserId);
            var (cleared, _, clearedInWip) = await BoxAsync(boxes[1]);
            Assert.False(clearedInWip);
            Assert.Null(cleared.WorkOrderId);
            using (var scope = NewScope(out var db))
            {
                var h = await db.ContainerUnitHistories.IgnoreQueryFilters()
                    .Where(x => x.ContainerUnitId == boxes[1]).OrderByDescending(x => x.Id).FirstAsync();
                Assert.Equal((ContainerUnitActions.Clear, Wo, (int?)10, (decimal?)10m), (h.Action, h.WorkOrderId, h.OperationSeq, h.Qty));
            }

            // 工單結案（例：入庫時沒用到這個箱子）→ 不必任何人處理，箱子自動回到可用
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                wo.Status = WorkOrderStatus.Closed;
                await db.SaveChangesAsync();
            }
            Assert.False((await BoxAsync(boxes[0])).InWip);
            using (var scope = NewScope(out _))
                Assert.Equal(boxes.OrderBy(b => b, StringComparer.Ordinal).ToArray(),
                    (await Svc<IContainerUnitService>(scope).EmptyUnitIdsAsync(BoxType, 5)).ToArray());
        }
    }
}
