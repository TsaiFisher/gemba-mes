using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 分箱完工入庫（container-tracking.md §8 L1+）：完工量依每箱數量拆成多個批號入庫。
    ///
    /// 釘的是兩件事：
    ///   - 分箱只改「放在哪些批號」，不改帳：總入庫量＝完工量、每箱單位成本相同、存貨價值與不分箱一樣
    ///   - 一箱一個批號的前提（批號不得與既有批號重複）不成立時整張不入庫——
    ///     任何一箱撞到既有批號都會和舊庫存合成一筆餘額，掃標籤就不再等於「這一箱」
    /// </summary>
    [Collection("Integration")]
    public class MfgCompletionBoxingFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGBOX";

        private const string Fg    = "FLOW-BOX-FG";
        private const string Batch = "FLOW-BOX-PB";
        private const string Wo    = "FLOW-BOX-WO";

        private static readonly DateOnly StockDate = new(2097, 6, 1);

        public MfgCompletionBoxingFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "分箱流程成品"));
            using var scope = NewScope(out var db);
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
            await db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId.StartsWith(UnitPrefix)).ExecuteDeleteAsync();
            await db.ContainerUnits.IgnoreQueryFilters().Where(u => u.ContainerUnitId.StartsWith(UnitPrefix)).ExecuteDeleteAsync();
            if (!await db.Containers.AnyAsync(c => c.ContainerCode == BoxType))
                db.Containers.Add(new SmartFactory.MES.Modules.BAS.Models.Container
                    { ContainerCode = BoxType, ContainerName = "分箱流程週轉箱", MaxCapacity = 30m, IsActive = true });
            await db.SaveChangesAsync();
        }

        private const string BoxType    = "FLOW-BOX-T";
        private const string UnitPrefix = "ZZBOXU";

        /// <summary>完工 100、人工＋製費 2,000（無用料）的單道工序工單。</summary>
        private async Task SeedCompletedWorkOrderAsync()
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-BOX-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = 100m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg, RoutingId = "FLOW-BOX-RT",
                Quantity = 100m, Status = WorkOrderStatus.Completed,
                ActualLaborCost = 800m, ActualOverheadCost = 1_200m, PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WC", UnitTime = 1m });
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = "FLOW-BOX-DP", WorkOrderId = Wo, OperationSeq = 10,
                Quantity = 100m, QtyDone = 100m, Status = DispatchStatus.Closed, PlantId = Plant,
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task 分箱入庫_一箱一個批號_總量與單位成本不變()
        {
            await SeedCompletedWorkOrderAsync();

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(30m, "BOX-A"));

            using (var scope = NewScope(out var db))
            {
                var lots = await db.Inventories.IgnoreQueryFilters()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Fg)
                    .OrderBy(i => i.LotNo)
                    .Select(i => new { i.LotNo, i.QtyOnHand })
                    .ToListAsync();
                // 批號前綴留空 → 用工單號；最後一箱裝餘數
                Assert.Equal(
                    new[] { ($"{Wo}-01", 30m), ($"{Wo}-02", 30m), ($"{Wo}-03", 30m), ($"{Wo}-04", 10m) },
                    lots.Select(l => (l.LotNo, l.QtyOnHand)).ToArray());

                var moves = await db.StockMovements.IgnoreQueryFilters()
                    .Where(m => m.PlantId == Plant && m.DocNo == Wo && m.TypeCode == "IT")
                    .ToListAsync();
                Assert.Equal(4, moves.Count);
                Assert.All(moves, m => Assert.Equal(20m, m.UnitCost));                 // 2,000 / 100，每箱相同
                Assert.Equal(2_000m, moves.Sum(m => m.TotalCost ?? 0m));               // 與不分箱一樣

                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Closed, wo.Status);
            }
        }

        [Fact]
        public async Task 分箱入庫_批號與既有批號重複_整張不入庫()
        {
            await SeedCompletedWorkOrderAsync();

            // 同品號已用過「LOT-02」——即使現在餘額是什麼都不重要，用過就不能再當另一箱
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-BOX-OPEN", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "LOT-02",
                    Qty = 5m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 20m,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out _))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: "LOT", new CompletionBoxing(30m)));

            using (var scope = NewScope(out var db))
            {
                Assert.False(await db.StockMovements.IgnoreQueryFilters()
                    .AnyAsync(m => m.PlantId == Plant && m.DocNo == Wo));
                Assert.Equal(5m, await QtyOnHandAsync(db, Plant, Fg));
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Completed, wo.Status);                    // 仍可改前綴後重新入庫
            }
        }

        [Fact]
        public async Task 分箱入庫_箱數超過上限_整張不入庫()
        {
            await SeedCompletedWorkOrderAsync();

            // 每箱 0.05 → 2,000 箱，超過 999（每箱數量打錯的典型形狀）
            using (var scope = NewScope(out _))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(0.05m)));

            using (var scope = NewScope(out var db))
                Assert.False(await db.StockMovements.IgnoreQueryFilters()
                    .AnyAsync(m => m.PlantId == Plant && m.DocNo == Wo));
        }

        // ── 指定箱號（container-tracking.md §9：箱子重複使用＋固定 QR）──────────────

        private async Task<IReadOnlyList<string>> CreateUnitsAsync(int n)
        {
            using var scope = NewScope(out _);
            return await Svc<IContainerUnitService>(scope).CreateBatchAsync(BoxType, UnitPrefix, n, UserId);
        }

        [Fact]
        public async Task 指定箱號入庫_每箱批號依序綁到箱子_出完後箱子可再用()
        {
            await SeedCompletedWorkOrderAsync();
            var units = await CreateUnitsAsync(4);

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(30m, BoxType, units, UserId));

            using (var scope = NewScope(out var db))
            {
                var bound = await db.ContainerUnits.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => units.Contains(u.ContainerUnitId)).OrderBy(u => u.ContainerUnitId).ToListAsync();
                Assert.Equal(new[] { $"{Wo}-01", $"{Wo}-02", $"{Wo}-03", $"{Wo}-04" }, bound.Select(u => u.LotNo).ToArray());
                Assert.All(bound, u => Assert.Equal(Fg, u.ProductCode));
                Assert.All(bound, u => Assert.Equal(Plant, u.PlantId));
                var svc = Svc<IContainerUnitService>(scope);
                Assert.Equal(10m, await svc.BoundQtyAsync(bound[3]));      // 末箱裝餘數
                Assert.Equal(4, await db.ContainerUnitHistories.IgnoreQueryFilters()
                    .CountAsync(h => units.Contains(h.ContainerUnitId) && h.Action == ContainerUnitActions.Bind && h.DocNo == Wo));
            }

            // 箱子還裝著貨：再拿來裝別的要擋
            using (var scope = NewScope(out _))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Svc<IContainerUnitService>(scope).TakeEmptyForBindingAsync(new[] { units[0] }, BoxType));

            // 第 1 箱整箱出掉（報廢）→ 帳上歸零即視為空箱，可再裝
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-BOX-OUT", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = $"{Wo}-01",
                    Qty = 30m, TypeCode = "IJ", TypeCategory = "Scrap",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out _))
            {
                var svc = Svc<IContainerUnitService>(scope);
                var reusable = await svc.TakeEmptyForBindingAsync(new[] { units[0] }, BoxType);
                Assert.Single(reusable);
                Assert.Equal(new[] { units[0] }, await svc.EmptyUnitIdsAsync(BoxType, 10));
            }
        }

        [Fact]
        public async Task 指定箱號_其中一箱還裝著貨_整張不入庫也不綁箱()
        {
            await SeedCompletedWorkOrderAsync();
            var units = await CreateUnitsAsync(4);

            // 先讓第 2 個箱子裝著別的批號的貨
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-BOX-OPEN2", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "OLD-LOT",
                    Qty = 5m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 20m,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                var u = await db.ContainerUnits.SingleAsync(x => x.ContainerUnitId == units[1]);
                Svc<IContainerUnitService>(scope).Bind(u, Fg, "OLD-LOT", 5m, "FLOW-BOX-OPEN2", UserId);
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out _))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(30m, BoxType, units, UserId)));

            using (var scope = NewScope(out var db))
            {
                Assert.False(await db.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.PlantId == Plant && m.DocNo == Wo));
                Assert.False(await db.ContainerUnits.IgnoreQueryFilters()
                    .AnyAsync(u => units.Contains(u.ContainerUnitId) && u.BoundDocNo == Wo));
            }
        }

        [Fact]
        public async Task 指定箱號數量與箱數不符_整張不入庫()
        {
            await SeedCompletedWorkOrderAsync();
            var units = await CreateUnitsAsync(3);   // 100 ÷ 30 要 4 箱

            using (var scope = NewScope(out _))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Svc<IProductionCompletionService>(scope)
                    .CompleteAsync(Wo, Plant, Wh, Loc, lotNo: null, new CompletionBoxing(30m, BoxType, units, UserId)));

            using (var scope = NewScope(out var db))
                Assert.False(await db.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.PlantId == Plant && m.DocNo == Wo));
        }
    }
}
