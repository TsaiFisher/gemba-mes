using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 作廢要沖的是**三份帳**：在手量、總帳、庫存成本。前兩份原本就有
    /// （<see cref="PostAndVoidFlowTests"/> 釘住），第三份一直是空的——
    /// <c>VoidDocAsync</c> 只呼叫 <c>UpdateQtyAsync</c>，成本一行都沒碰。
    ///
    /// 2026-09-04 Wave 3 銷退作廢實測撞到：均價被汙染成 895.02，作廢後數量正確地回到 3，
    /// 均價卻停在 895.02。之後每一次出庫的 COGS 都以那個錯的均價認列，而且不會自己收斂。
    ///
    /// 為什麼沒有任何既有防線會叫：
    ///   · 傳票——<c>ReverseJournalAsync</c> 把原分錄借貸對調，張張平衡、科目淨額歸零；
    ///   · <c>INV_ROLLFORWARD</c>——數量型稽核，而數量完全正確；
    ///   · <see cref="PostAndVoidFlowTests"/>——它作廢的是**唯一**一筆入庫，
    ///     沖完在手量歸零，均價留什麼值都不影響下一筆（分母是 0）。
    ///     所以「沖完之後還有庫存」是這支測試唯一非測不可的形狀。
    /// </summary>
    [Collection("Integration")]
    public class VoidCostReversalFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZVOIDCOST";

        private const string MaMat   = "FLOW-VC-MA";
        private const string FifoCat = "FLOW-VC-CAT-FIFO";
        private const string FifoMat = "FLOW-VC-FIFO";

        private static readonly DateOnly D = new(2097, 3, 10);

        public VoidCostReversalFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((MaMat, "作廢成本沖銷－移動平均料"));

            using var scope = NewScope(out var db);
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == FifoCat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = FifoCat, CategoryName = "作廢成本沖銷（FIFO）",
                    CostMethod = CostMethod.Fifo, IsActive = true,
                });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == FifoMat))
                db.Products.Add(new Product
                {
                    ProductCode = FifoMat, ProductName = "作廢成本沖銷－FIFO 料", CategoryCode = FifoCat,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 移動平均：兩筆入庫（100 個 @ 10、100 個 @ 30 → 均價 20），作廢第二筆。
        /// 兩筆單價刻意不同——相同的話均價本來就不會變，測了等於沒測。
        /// 修好＝均價回到 10；壞掉＝停在 20，而數量一樣是 100。
        /// </summary>
        [Fact]
        public async Task VoidInbound_RestoresMovingAverage_NotOnlyQuantity()
        {
            const decimal qty = 100m, priceA = 10m, priceB = 30m;

            await PostInboundAsync(MaMat, "FLOW-VC-MA-IN1", qty, priceA);
            await PostInboundAsync(MaMat, "FLOW-VC-MA-IN2", qty, priceB);

            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == MaMat);
                Assert.Equal(200m, inv.QtyOnHand);
                Assert.Equal(20m, inv.UnitCost);           // (100×10 + 100×30) / 200
            }

            await VoidAsync("FLOW-VC-MA-IN2");

            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == MaMat);
                Assert.Equal(qty, inv.QtyOnHand);
                Assert.Equal(priceA, inv.UnitCost);        // 沖掉第二筆＝回到第一筆之後的狀態
            }
        }

        /// <summary>
        /// FIFO：作廢入庫要把該單建立的成本層收回來。不收的話成本層總量永遠大於在手量，
        /// 之後領料會從一批「已經作廢掉的貨」身上取成本。
        /// </summary>
        [Fact]
        public async Task VoidInbound_RemovesFifoLayer_KeepsLayerLedgerInSyncWithOnHand()
        {
            const decimal qty = 60m, priceA = 50m, priceB = 200m;

            await PostInboundAsync(FifoMat, "FLOW-VC-FI-IN1", qty, priceA);
            await PostInboundAsync(FifoMat, "FLOW-VC-FI-IN2", qty, priceB);

            await VoidAsync("FLOW-VC-FI-IN2");

            using (var scope = NewScope(out var db))
            {
                var onHand = await QtyOnHandAsync(db, Plant, FifoMat);
                Assert.Equal(qty, onHand);

                var layers = await db.InventoryCostLayers.IgnoreQueryFilters()
                    .Where(l => l.PlantId == Plant && l.ProductCode == FifoMat).ToListAsync();
                Assert.Equal(onHand, layers.Sum(l => l.QtyRemaining));
                // 留下來的必須是第一批的成本，不是被作廢那批的
                Assert.Equal(qty * priceA, layers.Sum(l => l.QtyRemaining * l.UnitCost));
            }
        }

        /// <summary>
        /// FIFO：作廢**出庫**要把成本層還回去。不還的話在手量加回來、成本層卻沒有，
        /// 下一次領料就會撞上「成本層不足以扣除」——或者更糟，在還有別的層可扣時
        /// 靜靜地拿別批的成本頂上。
        /// </summary>
        [Fact]
        public async Task VoidOutbound_RestoresFifoLayer()
        {
            const decimal inQty = 80m, price = 25m, outQty = 30m;

            await PostInboundAsync(FifoMat, "FLOW-VC-FO-IN", inQty, price);
            await PostOutboundAsync(FifoMat, "FLOW-VC-FO-OUT", outQty);

            using (var scope = NewScope(out var db))
            {
                var layerQty = await db.InventoryCostLayers.IgnoreQueryFilters()
                    .Where(l => l.PlantId == Plant && l.ProductCode == FifoMat)
                    .SumAsync(l => l.QtyRemaining);
                Assert.Equal(inQty - outQty, layerQty);
            }

            await VoidAsync("FLOW-VC-FO-OUT");

            using (var scope = NewScope(out var db))
            {
                var onHand = await QtyOnHandAsync(db, Plant, FifoMat);
                Assert.Equal(inQty, onHand);

                var layers = await db.InventoryCostLayers.IgnoreQueryFilters()
                    .Where(l => l.PlantId == Plant && l.ProductCode == FifoMat).ToListAsync();
                Assert.Equal(onHand, layers.Sum(l => l.QtyRemaining));
                Assert.Equal(inQty * price, layers.Sum(l => l.QtyRemaining * l.UnitCost));
            }
        }

        // ── 輔助 ──

        // ── 2026-10-21 隨機序列測試（InventoryLedgerModelTests）抓到的三個缺陷 ─────────────

        /// <summary>
        /// FIFO 進貨自己那層已被出庫扣用：不准直接作廢（原本改從別層扣，數量對但總帳與成本層金額對不上）。
        /// 被擋下時什麼都不能動。
        /// </summary>
        [Fact]
        public async Task VoidInbound_FifoOwnLayerConsumed_IsRejected_AndNothingChanges()
        {
            await PostInboundAsync(FifoMat, "FLOW-VC-FC-IN1", 10m, 30m);
            await PostInboundAsync(FifoMat, "FLOW-VC-FC-IN2", 9m, 45m);
            await PostOutboundAsync(FifoMat, "FLOW-VC-FC-OUT", 12m);      // 扣光 IN1、再扣 IN2 的 2 個
            await PostInboundAsync(FifoMat, "FLOW-VC-FC-IN3", 5m, 60m);   // 儲位總量 12 ≥ 9：「可用量」擋不下來，要靠新規則

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VoidAsync("FLOW-VC-FC-IN2"));
            Assert.Contains("FLOW-VC-FC-IN2", ex.Message);

            using var scope = NewScope(out var db);
            Assert.Equal(12m, await QtyOnHandAsync(db, Plant, FifoMat));
            Assert.Equal(7m * 45m + 5m * 60m, await db.InventoryCostLayers.IgnoreQueryFilters()
                .Where(l => l.PlantId == Plant && l.ProductCode == FifoMat).SumAsync(l => l.QtyRemaining * l.UnitCost));

            // 先作廢扣用它的出庫，就可以作廢了
            await VoidAsync("FLOW-VC-FC-OUT");
            await VoidAsync("FLOW-VC-FC-IN2");
            using var scope2 = NewScope(out var db2);
            Assert.Equal(15m, await QtyOnHandAsync(db2, Plant, FifoMat));
        }

        /// <summary>
        /// 同一張單作廢兩次是 no-op：原本會把原單與第一次的沖銷列一起再沖，數量抵銷看不出來，
        /// 但移動平均的反向公式被套兩次，均價被改掉。
        /// </summary>
        [Fact]
        public async Task VoidTwice_IsNoOp_MovingAverageAndQuantityUnchanged()
        {
            await PostInboundAsync(MaMat, "FLOW-VC-VV-IN1", 100m, 10m);
            await PostInboundAsync(MaMat, "FLOW-VC-VV-IN2", 50m, 40m);
            await PostOutboundAsync(MaMat, "FLOW-VC-VV-OUT", 30m);
            await VoidAsync("FLOW-VC-VV-OUT");

            decimal qty1, cost1;
            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters().SingleAsync(i => i.PlantId == Plant && i.ProductCode == MaMat);
                (qty1, cost1) = (inv.QtyOnHand, inv.UnitCost);
            }

            await VoidAsync("FLOW-VC-VV-OUT");   // 第二次

            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters().SingleAsync(i => i.PlantId == Plant && i.ProductCode == MaMat);
                Assert.Equal((qty1, cost1), (inv.QtyOnHand, inv.UnitCost));
                Assert.Equal(2, await db.StockMovements.IgnoreQueryFilters().CountAsync(m => m.DocNo == "FLOW-VC-VV-OUT"));
            }
        }

        /// <summary>
        /// 移動平均料：作廢後剛好收光，但帳上剩的價值 ≠ 要收回的價值 → 擋下
        /// （原本放行，總帳存貨殘留一筆數量 0、永遠沖不掉的餘額）。
        /// </summary>
        [Fact]
        public async Task VoidInbound_MovingAverageFullReversalWithValueMismatch_IsRejected()
        {
            await PostInboundAsync(MaMat, "FLOW-VC-MR-IN1", 10m, 20m);
            await PostInboundAsync(MaMat, "FLOW-VC-MR-IN2", 4m, 40m);      // 均價 (200+160)/14 = 25.714…
            await PostOutboundAsync(MaMat, "FLOW-VC-MR-OUT", 10m);       // 剩 4 個 × 25.714 ≈ 102.86

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VoidAsync("FLOW-VC-MR-IN2"));   // 要收回 160
            Assert.Contains(MaMat, ex.Message);
            using var scope = NewScope(out var db);
            Assert.Equal(4m, await QtyOnHandAsync(db, Plant, MaMat));
        }

        private async Task PostInboundAsync(string product, string docNo, decimal qty, decimal price)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = product, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = D, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task PostOutboundAsync(string product, string docNo, decimal qty)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = product, LocationCode = Loc, LotNo = "",
                // 報廢而非生產領料：IO 屬於工單、不入總帳，不掛工單的 IO 會讓總帳存貨對明細帳永遠差這一筆（INV_GL_SUBLEDGER）
                Qty = qty, TypeCode = "IJ", TypeCategory = "Scrap",
                MovementDate = D, UomCode = Uom, StockUomCode = Uom, UnitPrice = null,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task VoidAsync(string docNo)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).VoidDocAsync(new VoidDocRequest
            {
                PlantId = Plant, DocNo = docNo, Reason = "作廢成本沖銷測試",
                CreatedBy = UserId, SetDocVoid = () => { },
            });
            await db.SaveChangesAsync();
        }
    }
}
