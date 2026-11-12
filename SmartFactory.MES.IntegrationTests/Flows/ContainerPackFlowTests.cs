using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 進貨上架裝箱、既有庫存裝箱／換箱（container-tracking.md §9.4 第 4 期）。
    ///
    /// 釘的是：
    ///   - 裝箱＝同儲位換成新批號（同廠調撥、撥入批號），數量與單位成本原樣帶過去，散裝那份只少裝走的量
    ///   - 換箱：來源用舊箱的批號，舊箱自動變空箱
    ///   - 作廢那張調撥＝貨換回原批號、箱子自動空箱
    ///   - 上架時指定空箱：系統產生新批號並綁箱；自己填用過的批號會被擋
    /// </summary>
    [Collection("Integration")]
    public class ContainerPackFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZBOXPK";

        private const string Fg      = "FLOW-BOXPK-FG";
        private const string BoxType = "FLOW-BOXPK-T";
        private const string Prefix  = "ZZPKB";
        private const string LoanIn  = "FLOW-BOXPK-IB1";

        private static readonly DateOnly StockDate = new(2097, 9, 1);

        public ContainerPackFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "裝箱流程成品"));
            using var scope = NewScope(out var db);
            await db.StockTransfers.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.LoanDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.ContainerUnits.IgnoreQueryFilters().Where(u => u.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            if (!await db.Containers.AnyAsync(c => c.ContainerCode == BoxType))
                db.Containers.Add(new Container { ContainerCode = BoxType, ContainerName = "裝箱流程週轉箱", MaxCapacity = 20m, IsActive = true });
            await db.SaveChangesAsync();
        }

        private async Task<IReadOnlyList<string>> CreateUnitsAsync(int n)
        {
            using var scope = NewScope(out _);
            return await Svc<IContainerUnitService>(scope).CreateBatchAsync(BoxType, Prefix, n, UserId);
        }

        /// <summary>散裝（無批號）50 @12。</summary>
        private async Task SeedLooseStockAsync()
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = "FLOW-BOXPK-OPEN", LineNo = 1,
                ProductCode = Fg, LocationCode = Loc, LotNo = "",
                Qty = 50m, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 12m,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task<(string TransferId, string Lot)> PackAsync(string box, string? sourceLot, decimal qty)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = Plant;
            return await Svc<IContainerPackService>(scope).PackAsync(box, Fg, Loc, sourceLot, qty, UserId);
        }

        private static Task<decimal> LotQtyAsync(ApplicationDbContext db, string plant, string lot) =>
            db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == plant && i.ProductCode == Fg && i.LotNo == lot)
                .SumAsync(i => i.QtyOnHand);

        [Fact]
        public async Task 散裝庫存裝箱_換新批號成本不變_換箱舊箱自動空_作廢調撥回原批號()
        {
            await SeedLooseStockAsync();
            var boxes = await CreateUnitsAsync(2);

            var (t1, lot1) = await PackAsync(boxes[0], null, 20m);
            Assert.Equal($"{t1}-01", lot1);
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(30m, await LotQtyAsync(db, Plant, ""));
                Assert.Equal(20m, await LotQtyAsync(db, Plant, lot1));
                var moves = await db.StockMovements.IgnoreQueryFilters().AsNoTracking().Where(m => m.DocNo == t1).ToListAsync();
                Assert.Equal(2, moves.Count);
                Assert.Equal(moves[0].UnitCost, moves[1].UnitCost);                         // 成本原樣帶過去
                var doc = await db.StockTransfers.IgnoreQueryFilters().Include(d => d.Lines).SingleAsync(d => d.StockTransferId == t1);
                Assert.Equal(DocStatus.Posted, doc.Status);
                Assert.Equal((null, lot1), (doc.Lines.Single().LotNo, doc.Lines.Single().ToLotNo));
                var u = await db.ContainerUnits.IgnoreQueryFilters().SingleAsync(x => x.ContainerUnitId == boxes[0]);
                Assert.Equal((lot1, t1), (u.LotNo, u.BoundDocNo));
            }

            // 換箱：A 箱整箱倒進 B 箱 → A 箱批號歸零自動空箱
            var (t2, lot2) = await PackAsync(boxes[1], lot1, 20m);
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(0m, await LotQtyAsync(db, Plant, lot1));
                Assert.Equal(20m, await LotQtyAsync(db, Plant, lot2));
                var svc = Svc<IContainerUnitService>(scope);
                Assert.Equal(new[] { boxes[0] }, await svc.EmptyUnitIdsAsync(BoxType, 5));
            }

            // 作廢換箱那張調撥 → 貨回到 A 箱的批號，B 箱自動空、A 箱又是使用中
            using (var scope = NewScope(out var db))
            {
                var doc = await db.StockTransfers.IgnoreQueryFilters().SingleAsync(d => d.StockTransferId == t2);
                var moves = await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == t2).ToListAsync();
                await Svc<IInventoryPostingService>(scope).VoidByMovementsAsync(moves, new VoidByMovementsRequest
                {
                    VoidDate = DateOnly.FromDateTime(DateTime.Today), Reason = "test", CreatedBy = UserId,
                    SetDocVoid = () => { doc.Status = DocStatus.Void; },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(20m, await LotQtyAsync(db, Plant, lot1));
                Assert.Equal(0m, await LotQtyAsync(db, Plant, lot2));
                Assert.Equal(new[] { boxes[1] }, await Svc<IContainerUnitService>(scope).EmptyUnitIdsAsync(BoxType, 5));
            }
        }

        [Fact]
        public async Task 庫存不足或箱子不空_裝箱整筆不做()
        {
            await SeedLooseStockAsync();
            var boxes = await CreateUnitsAsync(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => PackAsync(boxes[0], null, 60m));      // 只有 50
            using (var scope = NewScope(out var db))
            {
                Assert.False(await db.StockTransfers.IgnoreQueryFilters().AnyAsync(d => d.PlantId == Plant));
                Assert.Equal(50m, await LotQtyAsync(db, Plant, ""));
                Assert.Null((await db.ContainerUnits.IgnoreQueryFilters().SingleAsync(x => x.ContainerUnitId == boxes[0])).LotNo);
            }

            await PackAsync(boxes[0], null, 10m);
            await Assert.ThrowsAsync<InvalidOperationException>(() => PackAsync(boxes[0], null, 10m));      // 已裝著貨
        }

        // ── 上架裝箱 ─────────────────────────────────────────────────────

        private async Task SeedLoanInAsync()
        {
            using var scope = NewScope(out var db);
            var doc = new LoanDoc
            {
                LoanDocId = LoanIn, LoanDate = StockDate, TypeCode = "IB", TypeCategory = "Loan",
                EntityType = "Customer", EntityCode = "FLOW-CUS",
                Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            doc.Lines.Add(new LoanDocLine
            {
                LoanDocId = LoanIn, LineNo = 1, ProductCode = Fg, Qty = 30m, UomCode = Uom,
                StockQty = 30m, StockUomCode = Uom, UnitPrice = 12m, WhCode = Wh,
            });
            db.LoanDocs.Add(doc);
            await db.SaveChangesAsync();
        }

        private WarehouseApiController NewWarehouseController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new WarehouseApiController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IUomService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IAPInvoiceService>(), sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<IJournalEntryService>(),
                sp.GetRequiredService<IARCreditNoteService>(), sp.GetRequiredService<IAPDebitNoteService>(),
                sp.GetRequiredService<IInventoryReservationService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private async Task<IActionResult> PutAwayAsync(decimal qty, string lot, string? box)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = Plant;
            return await NewWarehouseController(scope, db).PutAway(new WarehouseApiController.PutAwayRequest
            {
                TypeCode = "IB", DocNo = LoanIn, LineNo = 1, WhCode = Wh, LocationCode = Loc, LotNo = lot, Qty = qty,
                ContainerUnitId = box,
            });
        }

        [Fact]
        public async Task 上架指定空箱_系統產生新批號並綁箱_用過的批號擋下()
        {
            await SeedLoanInAsync();
            var boxes = await CreateUnitsAsync(2);

            Assert.IsType<OkObjectResult>(await PutAwayAsync(20m, "", boxes[0].ToLowerInvariant()));
            var lot = $"{LoanIn}-01-01";
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(20m, await LotQtyAsync(db, Plant, lot));
                var u = await db.ContainerUnits.IgnoreQueryFilters().SingleAsync(x => x.ContainerUnitId == boxes[0]);
                Assert.Equal((Fg, lot, LoanIn), (u.ProductCode, u.LotNo, u.BoundDocNo));
            }

            // 同一個箱子再上架：裝著貨，擋
            Assert.IsType<BadRequestObjectResult>(await PutAwayAsync(5m, "", boxes[0]));
            // 另一個空箱但指定用過的批號：擋（箱內數量會和既有那批混在一起）
            Assert.IsType<BadRequestObjectResult>(await PutAwayAsync(5m, lot, boxes[1]));
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(20m, (await db.LoanDocs.IgnoreQueryFilters().Include(d => d.Lines)
                    .SingleAsync(d => d.LoanDocId == LoanIn)).Lines.Single().QtyPutAway);
                Assert.Null((await db.ContainerUnits.IgnoreQueryFilters().SingleAsync(x => x.ContainerUnitId == boxes[1])).LotNo);
            }

            // 不指定箱子照舊
            Assert.IsType<OkObjectResult>(await PutAwayAsync(10m, "", null));
        }
    }
}
