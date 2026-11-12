using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 同廠調撥要能搬**有批號**的庫存（container-tracking.md §9.3）。
    ///
    /// 修正前 <c>PostSamePlantTransferAsync</c> 的可用量、撥出、撥入、成本層一律寫死空批號：
    /// 有批號的庫存（含完工分箱入庫的每一箱）可用量被判為 0 而搬不動；
    /// 即使同儲位剛好有足量的無批號庫存，搬走的也是**那一份**，批號那份原地不動。
    ///
    /// 釘的是：批號原樣帶到目的儲位、成本層跟著批號走、無批號庫存不被波及、作廢回得去。
    /// </summary>
    [Collection("Integration")]
    public class SamePlantTransferLotFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZTRLOT";

        private const string Cat = "FLOW-TRL-FIFOCAT";
        private const string Mat = "FLOW-TRL-MAT";
        private const string Doc = "FLOW-TRL-01";
        private const string Lot = "FLOW-TRL-WO-01";

        private static readonly DateOnly StockDate    = new(2098, 7, 1);
        private static readonly DateOnly TransferDate = new(2098, 7, 20);

        // 批號那份單價與無批號那份不同：搬錯份的話成本一眼看得出來
        private const decimal LotQty = 30m, LotCost = 50m;
        private const decimal LooseQty = 100m, LooseCost = 10m;

        private string Loc2 => $"{Plant}-LOC2";

        public SamePlantTransferLotFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync();
            using var scope = NewScope(out var db);
            await db.StockTransfers.IgnoreQueryFilters()
                .Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == Wh && l.LocationCode == Loc2))
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = Wh, LocationCode = Loc2, LocationName = "批號調撥目的儲位",
                });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task 調撥有批號庫存_批號與成本層一起到目的儲位_無批號那份不動()
        {
            await SeedStockAsync();

            await TransferAsync(LotQty, Lot);

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(0m,       await OnHandAsync(db, Loc,  Lot));
                Assert.Equal(LotQty,   await OnHandAsync(db, Loc2, Lot));
                Assert.Equal(LooseQty, await OnHandAsync(db, Loc,  ""));
                Assert.Equal(0m,       await OnHandAsync(db, Loc2, ""));

                var moves = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == Doc).ToListAsync();
                Assert.Equal(2, moves.Count);
                Assert.All(moves, m => Assert.Equal(Lot, m.LotNo));
                Assert.All(moves, m => Assert.Equal(LotCost, m.UnitCost));   // 不是無批號那份的 10

                var layer = Assert.Single(await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.ProductCode == Mat && l.LocationCode == Loc2 && l.QtyRemaining > 0).ToListAsync());
                Assert.Equal(Lot, layer.LotNo);
                Assert.Equal(LotCost, layer.UnitCost);

                Assert.Empty(await Svc<IDataAuditService>(scope).AuditFifoLayerBalanceAsync(Plant));
            }

            // 從目的儲位按批號出得掉，且成本是那一箱的
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-TRL-ISSUE", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc2, LotNo = Lot,
                    Qty = LotQty, TypeCode = "IJ", TypeCategory = "Scrap",
                    MovementDate = TransferDate, UomCode = Uom, StockUomCode = Uom,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                var issue = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == "FLOW-TRL-ISSUE");
                Assert.Equal(LotCost, issue.UnitCost);
            }
        }

        [Fact]
        public async Task 批號數量不足_即使同儲位無批號庫存夠也擋下()
        {
            await SeedStockAsync();

            // 批號只有 30；同儲位無批號有 100——修正前看的是無批號那份，會放行並搬錯份
            await Assert.ThrowsAsync<InvalidOperationException>(() => TransferAsync(LotQty + 10m, Lot));

            using var scope = NewScope(out var db);
            Assert.False(await db.StockMovements.IgnoreQueryFilters().AnyAsync(m => m.DocNo == Doc));
            Assert.Equal(LotQty,   await OnHandAsync(db, Loc, Lot));
            Assert.Equal(LooseQty, await OnHandAsync(db, Loc, ""));
        }

        [Fact]
        public async Task 作廢有批號的調撥_批號回到來源儲位()
        {
            await SeedStockAsync();
            await TransferAsync(LotQty, Lot);

            using (var scope = NewScope(out var db))
            {
                var doc = await db.StockTransfers.IgnoreQueryFilters().SingleAsync(d => d.StockTransferId == Doc);
                var moves = await db.StockMovements.IgnoreQueryFilters()
                    .Where(m => m.DocNo == Doc && (m.TypeCode == "WP" || m.TypeCode == "WI")).ToListAsync();
                await Svc<IInventoryPostingService>(scope).VoidByMovementsAsync(moves, new VoidByMovementsRequest
                {
                    VoidDate = TransferDate, Reason = "test", CreatedBy = UserId,
                    SetDocVoid = () => { doc.Status = DocStatus.Void; },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(LotQty, await OnHandAsync(db, Loc,  Lot));
                Assert.Equal(0m,     await OnHandAsync(db, Loc2, Lot));
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditFifoLayerBalanceAsync(Plant));
            }
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedStockAsync()
        {
            using (var scope = NewScope(out var db))
            {
                if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                    db.ProductCategories.Add(new ProductCategory
                    {
                        CategoryCode = Cat, CategoryName = "批號調撥 FIFO 類別",
                        CostMethod = CostMethod.Fifo, IsActive = true,
                    });
                var product = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.ProductCode == Mat);
                if (product == null)
                    db.Products.Add(new Product
                    {
                        ProductCode = Mat, ProductName = "批號調撥料", CategoryCode = Cat,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });
                else
                    product.CategoryCode = Cat;
                await db.SaveChangesAsync();
            }

            await ReceiveAsync("FLOW-TRL-OPEN1", LooseQty, LooseCost, "");
            await ReceiveAsync("FLOW-TRL-OPEN2", LotQty,   LotCost,   Lot);
        }

        private async Task ReceiveAsync(string docNo, decimal qty, decimal cost, string lot)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = lot,
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        /// <summary>單據照控制器的方式組請求（LotNo 由明細帶入），走過帳服務。</summary>
        private async Task TransferAsync(decimal qty, string lot)
        {
            using var scope = NewScope(out var db);
            var doc = new StockTransfer
            {
                StockTransferId = Doc, TransferDate = TransferDate,
                PlantId = Plant, FromWhCode = Wh, ToWhCode = Wh,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, CreatedByRoleId = RoleId,
            };
            doc.Lines.Add(new StockTransferLine
            {
                StockTransferId = Doc, LineNo = 1, ProductCode = Mat,
                Qty = qty, StockQty = qty, UomCode = Uom, StockUomCode = Uom,
                FromWhCode = Wh, FromLocationCode = Loc, ToWhCode = Wh, ToLocationCode = Loc2,
                LotNo = lot,
            });
            db.StockTransfers.Add(doc);

            await Svc<IInventoryPostingService>(scope).PostSamePlantTransferAsync(new PostSamePlantTransferRequest
            {
                PlantId = Plant, DocNo = Doc, MovementDate = TransferDate, CreatedBy = UserId,
                Lines = doc.Lines.Select(l => new TransferLine
                {
                    LineNo = l.LineNo, ProductCode = l.ProductCode,
                    FromLocationCode = l.FromLocationCode ?? "", ToLocationCode = l.ToLocationCode ?? "",
                    LotNo = l.LotNo ?? "",
                    StockQty = l.StockQty, UomCode = l.UomCode ?? "", StockUomCode = l.StockUomCode ?? "",
                    SetQtyPutAway = q => { l.QtyPutAway = q; },
                }).ToList(),
                SetDocPosted = () => { doc.Status = DocStatus.Posted; },
            });
            await db.SaveChangesAsync();
        }

        private async Task<decimal> OnHandAsync(Data.ApplicationDbContext db, string loc, string lot)
            => await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == Mat && i.LocationCode == loc && i.LotNo == lot)
                .SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m;
    }
}
