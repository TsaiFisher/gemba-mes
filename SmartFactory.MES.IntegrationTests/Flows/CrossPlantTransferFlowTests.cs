using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-9：跨廠調撥。
    ///
    /// 作業書原本的通過標準只寫「兩廠庫存一增一減，在途量正確」——那是數量的事。
    /// 但調撥搬動的不只是數量，還有**價值**：來源廠的存貨少了 30 個 × 40 元，
    /// 目的廠多了同樣一筆。總帳的 <c>JournalEntry.PlantId</c> 是有廠別的，
    /// 全站的稽核與對帳（<c>GlNetByAccountAsync</c>、<c>DataAuditService</c>）也都逐廠在跑，
    /// 所以「每一廠的存貨科目要等於該廠的庫存明細帳」是這個系統確實在主張的不變量。
    ///
    /// 兩段式過帳（XP 撥出 → XI 撥入）目前完全不產生任何分錄，於是：
    ///   來源廠：貨走了，存貨科目沒動 → 總帳比明細帳多出那批貨的價值
    ///   目的廠：貨到了，存貨科目沒動 → 總帳比明細帳少了同一筆
    /// 兩廠合計剛好相抵，所以**全公司層級看起來是對的**，逐廠看才會發現各差一筆，
    /// 而且差額不會隨時間收斂——每調撥一次就多差一次。
    ///
    /// 規格面也確實沒寫過：`inventory-advanced.md` §2-2 與 `inventory.md` 四-C
    /// 只描述數量與成本傳遞，沒有任何一段提到分錄。這是規格缺口，不是實作偏離規格。
    /// </summary>
    [Collection("Integration")]
    public class CrossPlantTransferFlowTests : FlowTestBase
    {
        protected override string Plant => FromPlant;
        protected override IEnumerable<string> ExtraPlants => new[] { ToPlant };

        private const string FromPlant = "ZZXPA";
        private const string ToPlant   = "ZZXPB";
        private const string Mat       = "FLOW-XP-MAT";
        private const string Doc       = "FLOW-XP-01";

        private static readonly DateOnly StockDate    = new(2098, 5, 1);
        private static readonly DateOnly TransferDate = new(2098, 5, 10);

        private const decimal OpenQty = 100m, UnitCost = 40m, MoveQty = 30m;

        private string ToWh  => $"{ToPlant}-WH";
        private string ToLoc => $"{ToPlant}-LOC";

        public CrossPlantTransferFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "跨廠調撥測試料"));
            using var scope = NewScope(out var db);
            await db.CrossPlantTransfers.IgnoreQueryFilters()
                .Where(d => d.PlantId == FromPlant || d.ToPlantId == FromPlant).ExecuteDeleteAsync();

            // 目的廠的主檔：ResetAsync 只建本廠的，對面廠得自己補。
            if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == ToPlant))
                db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = ToPlant, Name = $"流程測試廠 {ToPlant}" });
            db.Warehouses.Add(new Warehouse { WhCode = ToWh, WhName = "對面廠倉", PlantId = ToPlant });
            db.StorageLocations.Add(new StorageLocation { WhCode = ToWh, LocationCode = ToLoc, LocationName = "對面廠儲位" });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 撥出 → 收貨走完之後，兩件事都要成立：
        ///   ① 數量：來源廠 −30、目的廠 +30，且目的廠承接來源廠的單位成本（作業書原本的標準）
        ///   ② 價值：**每一廠**的存貨科目淨額等於該廠庫存明細帳合計
        ///
        /// ②在中間狀態（已撥出、未收貨）也必須成立——那段期間貨的價值不在任何一廠的
        /// 庫存明細帳裡，帳上得有個地方放著，否則來源廠的總帳就會憑空多出一筆存貨。
        /// </summary>
        [Fact]
        public async Task CrossPlantTransfer_MovesValueBetweenPlants_NotJustQuantity()
        {
            await SeedOpeningStockAsync();
            await SeedTransferDocAsync();

            // ── ① 來源廠確認發出（XP）─────────────────────────────────────
            await ConfirmAsync(FromPlant, c => c.Confirm(Doc));

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(OpenQty - MoveQty, await QtyOnHandAsync(db, FromPlant, Mat));
                Assert.Equal(0m, await QtyOnHandAsync(db, ToPlant, Mat));

                var doc = await db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(d => d.CrossPlantTransferId == Doc);
                Assert.Equal(DocStatus.InTransit, doc.Status);

                // 在途期間：貨已離開來源廠的庫存明細帳，來源廠總帳不得還掛著那批貨
                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
            }

            // ── ② 目的廠收貨（XI）────────────────────────────────────────
            await ConfirmAsync(ToPlant, c => c.Receive(Doc));

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(OpenQty - MoveQty, await QtyOnHandAsync(db, FromPlant, Mat));
                Assert.Equal(MoveQty, await QtyOnHandAsync(db, ToPlant, Mat));

                // 成本傳遞：目的廠承接來源廠出庫當下的單位成本
                var toInv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == ToPlant && i.ProductCode == Mat);
                Assert.Equal(UnitCost, toInv.UnitCost);

                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                await AssertPlantGlMatchesLedgerAsync(db, ToPlant);

                await AssertEveryEntryBalancedAsync(db, FromPlant);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(FromPlant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(ToPlant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(FromPlant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(ToPlant));
            }
        }

        /// <summary>
        /// 作業書 §7.1 的 4-3：作廢回沖「不得只沖一半」。
        /// 跨廠調撥的作廢要同時回沖**兩廠**的庫存與總帳，回到調撥前的狀態。
        /// </summary>
        [Fact]
        public async Task VoidingPostedTransfer_ReversesBothPlantsStockAndGl()
        {
            await SeedOpeningStockAsync();
            await SeedTransferDocAsync();
            await ConfirmAsync(FromPlant, c => c.Confirm(Doc));
            await ConfirmAsync(ToPlant, c => c.Receive(Doc));

            await ConfirmAsync(FromPlant, c => c.Void(Doc, "測試作廢"));

            using var scope = NewScope(out var db);

            Assert.Equal(OpenQty, await QtyOnHandAsync(db, FromPlant, Mat));
            Assert.Equal(0m, await QtyOnHandAsync(db, ToPlant, Mat));

            await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
            await AssertPlantGlMatchesLedgerAsync(db, ToPlant);

            // 廠際往來走完一趟又回沖，兩廠都必須歸零——這是清算科目該有的樣子
            var fromGl = await GlNetByAccountAsync(db, FromPlant);
            var toGl   = await GlNetByAccountAsync(db, ToPlant);
            Assert.Equal(0m, Net(fromGl, StandardAccounts.InterPlantClearingCode));
            Assert.Equal(0m, Net(toGl, StandardAccounts.InterPlantClearingCode));
        }

        /// <summary>
        /// 作業書 §7.1 的 4-10：中途反悔。
        /// 「撤回發出」把單子退回草稿，可以再確認一次——於是
        /// 確認→撤回→確認→撤回 是合法且會發生的循環。
        ///
        /// 原本的沖銷防護是「已有反向分錄就整個跳過」，在只能作廢一次的單據上等價，
        /// 但在這條循環裡，第二次撤回會完全不回沖，第二次確認的分錄就永遠留著。
        /// 改看「原分錄 − 已沖銷」的淨額之後，每一圈都會回到原點。
        /// </summary>
        [Fact]
        public async Task ConfirmCancelCycle_LeavesNoAccumulatingJournalResidue()
        {
            await SeedOpeningStockAsync();
            await SeedTransferDocAsync();

            for (var round = 1; round <= 2; round++)
            {
                await ConfirmAsync(FromPlant, c => c.Confirm(Doc));
                await ConfirmAsync(FromPlant, c => c.CancelConfirm(Doc));

                using var scope = NewScope(out var db);
                Assert.Equal(OpenQty, await QtyOnHandAsync(db, FromPlant, Mat));
                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);

                var gl = await GlNetByAccountAsync(db, FromPlant);
                Assert.Equal(0m, Net(gl, StandardAccounts.InterPlantClearingCode));

                var doc = await db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(d => d.CrossPlantTransferId == Doc);
                Assert.Equal(DocStatus.Draft, doc.Status);
            }
        }

        /// <summary>
        /// 撤回發出後來源廠均價變了（又進一批貨）、再撥出、目的廠收貨：撥入要承接**第二次**撥出的單價。
        /// 原本收貨取「這一行任意一筆 XP」，可能拿到第一次（已撤回）的成本，
        /// 來源廠 1450 借新成本、目的廠貸舊成本，兩廠合計永遠沖不平（2026-10-23 跨廠隨機序列抓到）。
        /// </summary>
        [Fact]
        public async Task ReconfirmAfterCostChange_ReceiveCarriesLatestShipCost_ClearingNetsToZero()
        {
            await SeedOpeningStockAsync();                                   // 100 @40
            await SeedTransferDocAsync();
            await ConfirmAsync(FromPlant, c => c.Confirm(Doc));              // 第一次撥出 30 @40
            await ConfirmAsync(FromPlant, c => c.CancelConfirm(Doc));

            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = FromPlant, DocNo = "FLOW-XP-OPEN2", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = 100m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 80m,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });                                                          // 均價 (100×40+100×80)/200 = 60
                await db.SaveChangesAsync();
            }

            await ConfirmAsync(FromPlant, c => c.Confirm(Doc));              // 第二次撥出 30 @60
            await ConfirmAsync(ToPlant, c => c.Receive(Doc));

            using (var scope = NewScope(out var db))
            {
                var toInv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == ToPlant && i.ProductCode == Mat);
                Assert.Equal(60m, toInv.UnitCost);

                var clearing = Net(await GlNetByAccountAsync(db, FromPlant), StandardAccounts.InterPlantClearingCode)
                             + Net(await GlNetByAccountAsync(db, ToPlant), StandardAccounts.InterPlantClearingCode);
                Assert.Equal(0m, clearing);
                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                await AssertPlantGlMatchesLedgerAsync(db, ToPlant);
            }
        }

        // ── 批號（container-tracking.md §9 第 3 期：箱子跟貨到別廠，批號要跟著走）──────

        private const string Lot = "FLOW-XP-LOT1";

        /// <summary>同儲位放兩份：無批號 100 @40、批號 20 @50（移動均價是全廠口徑，成本層的批號案例見 SamePlantTransferLotFlowTests）。</summary>
        private async Task SeedLotStockAsync()
        {
            await SeedOpeningStockAsync();
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = FromPlant, DocNo = "FLOW-XP-OPENLOT", LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = Lot,
                Qty = 20m, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 50m,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private static Task<decimal> LotQtyAsync(ApplicationDbContext db, string plant, string lot) =>
            db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == plant && i.ProductCode == Mat && i.LotNo == lot)
                .SumAsync(i => i.QtyOnHand);

        [Fact]
        public async Task 指定批號_撥出撥入都搬該批號_成本承接_作廢回原廠()
        {
            await SeedLotStockAsync();
            await SeedTransferDocAsync(Doc, Mat, 20m, Lot);

            await ConfirmAsync(FromPlant, c => c.Confirm(Doc));
            await ConfirmAsync(ToPlant, c => c.Receive(Doc));

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(0m, await LotQtyAsync(db, FromPlant, Lot));
                Assert.Equal(100m, await LotQtyAsync(db, FromPlant, ""));               // 無批號那份一動也沒動
                Assert.Equal(20m, await LotQtyAsync(db, ToPlant, Lot));
                // 移動均價是全廠口徑（不分批號），目的廠承接的是撥出當下凍結在 XP 上的單價
                var toInv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == ToPlant && i.ProductCode == Mat && i.LotNo == Lot);
                var xp = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == Doc && m.TypeCode == "XP");
                Assert.Equal(xp.UnitCost, toInv.UnitCost);
                Assert.All(await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == Doc).ToListAsync(), m => Assert.Equal(Lot, m.LotNo));
                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                await AssertPlantGlMatchesLedgerAsync(db, ToPlant);
            }

            await ConfirmAsync(FromPlant, c => c.Void(Doc, "測試作廢"));
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(20m, await LotQtyAsync(db, FromPlant, Lot));
                Assert.Equal(0m, await LotQtyAsync(db, ToPlant, Lot));
                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                await AssertPlantGlMatchesLedgerAsync(db, ToPlant);
            }
        }

        [Fact]
        public async Task 批號不足_即使無批號庫存夠也擋下()
        {
            await SeedLotStockAsync();
            await SeedTransferDocAsync(Doc, Mat, 30m, Lot);                             // 批號只有 20

            using var scope = NewScope(out var db);
            db.CurrentPlantId = FromPlant;
            var sp = new TestControllerServices(scope.ServiceProvider, db, FromPlant, RoleId);
            var ctrl = new CrossPlantTransferController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
            Assert.IsNotType<OkObjectResult>(await ctrl.Confirm(Doc));

            using var verify = NewScope(out var vdb);
            Assert.Equal(20m, await LotQtyAsync(vdb, FromPlant, Lot));
            Assert.Equal(100m, await LotQtyAsync(vdb, FromPlant, ""));
            Assert.Equal(DocStatus.Draft, (await vdb.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(d => d.CrossPlantTransferId == Doc)).Status);
        }

        /// <summary>
        /// 該廠的存貨科目（1401）淨額必須等於該廠庫存明細帳合計。
        /// 在途中的價值不算在明細帳裡（那時價值掛在 1450 廠際往來）。
        ///
        /// 明細帳要兩邊都加：移動平均的價值在 <c>Inventory.UnitCost</c>，
        /// FIFO 的價值在 <c>InventoryCostLayers</c>——而 FIFO 品項的
        /// <c>Inventory.UnitCost</c> 恆為 0。只算前者的話，FIFO 品項的明細帳一律是 0，
        /// 這個不變量在 FIFO 上就永遠是「總帳有、明細帳沒有」的假紅（或更糟：假綠）。
        /// 兩者互斥，直接相加不會重複計算。
        /// </summary>
        private static async Task AssertPlantGlMatchesLedgerAsync(ApplicationDbContext db, string plant)
        {
            var gl = await GlNetByAccountAsync(db, plant);
            var maValue = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == plant)
                .SumAsync(i => (decimal?)(i.QtyOnHand * i.UnitCost)) ?? 0m;
            var fifoValue = await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PlantId == plant)
                .SumAsync(l => (decimal?)(l.QtyRemaining * l.UnitCost)) ?? 0m;

            Assert.Equal(
                Math.Round(maValue + fifoValue, 2),
                Math.Round(Net(gl, StandardAccounts.RawMaterialInventoryCode), 2));
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync()
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = FromPlant, DocNo = "FLOW-XP-OPEN", LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = OpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = UnitCost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        // ── FIFO 品項（W4-F9／W4-F10）────────────────────────────────────
        //
        // FlowTestBase 把所有產品類別都種成 MovingAverage，於是上面那些測試從來沒有
        // 走到 FIFO 分支——而兩支調撥的撥出端當初是無條件讀 Inventory.UnitCost
        // （＝移動均價欄位，FIFO 品項恆為 0）。所以這裡自己種一個 FIFO 類別。

        private const string FifoCat  = "FLOW-XP-FIFO";
        private const string FifoMat  = "FLOW-XP-FMAT";
        private const string FifoDoc  = "FLOW-XP-F01";

        // 兩層不同單價，且**先進的那層比較貴**：這樣「取消耗層單價」與
        // 「取全部層的加權平均」會給出不同答案，測試才分得出來。
        private const decimal OldLayerQty = 40m, OldLayerCost = 300m;
        private const decimal NewLayerQty = 60m, NewLayerCost = 100m;
        private const decimal FifoMoveQty = 10m;

        private async Task SeedFifoStockAsync()
        {
            await SeedFifoMasterAsync();
            await ReceiveFifoLayerAsync("FLOW-XP-FOPEN1", StockDate,               OldLayerQty, OldLayerCost);
            await ReceiveFifoLayerAsync("FLOW-XP-FOPEN2", StockDate.AddDays(10),   NewLayerQty, NewLayerCost);
        }

        private async Task SeedFifoMasterAsync()
        {
            using (var scope0 = NewScope(out var db0))
            {
                if (!await db0.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == FifoCat))
                    db0.ProductCategories.Add(new ProductCategory
                    {
                        CategoryCode = FifoCat, CategoryName = "跨廠調撥 FIFO 類別",
                        CostMethod = CostMethod.Fifo, IsActive = true,
                    });
                if (!await db0.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == FifoMat))
                    db0.Products.Add(new Product
                    {
                        ProductCode = FifoMat, ProductName = "跨廠調撥 FIFO 料", CategoryCode = FifoCat,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });
                await db0.SaveChangesAsync();
            }
        }

        private async Task ReceiveFifoLayerAsync(string docNo, DateOnly date, decimal qty, decimal cost)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = FromPlant, DocNo = docNo, LineNo = 1,
                ProductCode = FifoMat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = date, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task<(decimal Qty, decimal Value)> LayersAsync(string plant, string product)
        {
            using var scope = NewScope(out var db);
            var rows = await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PlantId == plant && l.ProductCode == product)
                .Select(l => new { l.QtyRemaining, l.UnitCost }).ToListAsync();
            return (rows.Sum(r => r.QtyRemaining), rows.Sum(r => r.QtyRemaining * r.UnitCost));
        }

        /// <summary>
        /// FIFO 品項的跨廠調撥：價值要真的跟著貨走，而且走的是**先進先出那一層**的單價。
        ///
        /// 修正前這條路整條是啞的：撥出端讀 Inventory.UnitCost（FIFO 品項恆為 0）→
        /// XP 凍結成本 0 → 目的廠的成本承接被 <c>unitCost &gt; 0</c> 擋掉 →
        /// 廠際往來分錄金額 0 被略過 → 兩廠存貨科目都沒動；
        /// 同時來源廠的成本層一個都沒被消耗，在手量減了、層沒減，
        /// 成本層總量從此永久大於在手量。而數量全對、傳票全平（根本沒有傳票），
        /// 只有 FIFO_LAYER_QTY_MISMATCH 這一支稽核會叫。
        /// </summary>
        [Fact]
        public async Task CrossPlantTransfer_OfFifoProduct_ConsumesOldestLayer_AndCarriesItsCost()
        {
            await SeedFifoStockAsync();
            await SeedTransferDocAsync(FifoDoc, FifoMat, FifoMoveQty);

            var openQty = OldLayerQty + NewLayerQty;
            var openValue = OldLayerQty * OldLayerCost + NewLayerQty * NewLayerCost;

            await ConfirmAsync(FromPlant, c => c.Confirm(FifoDoc));

            using (var scope = NewScope(out var db))
            {
                // 成本層必須跟著在手量一起減——這是修正前完全沒發生的那一半
                var (qty, value) = await LayersAsync(FromPlant, FifoMat);
                Assert.Equal(openQty - FifoMoveQty, qty);
                Assert.Equal(openValue - FifoMoveQty * OldLayerCost, value);
                Assert.Equal(openQty - FifoMoveQty, await QtyOnHandAsync(db, FromPlant, FifoMat));

                // 凍結成本＝**被消耗那層**的單價（300），不是全部層的加權平均（180）
                var xp = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == FifoDoc && m.TypeCode == "XP");
                Assert.Equal(OldLayerCost, xp.UnitCost);

                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
            }

            await ConfirmAsync(ToPlant, c => c.Receive(FifoDoc));

            using (var scope = NewScope(out var db))
            {
                // 目的廠拿到的是**同單價的成本層**，不是一筆零元庫存
                var (toQty, toValue) = await LayersAsync(ToPlant, FifoMat);
                Assert.Equal(FifoMoveQty, toQty);
                Assert.Equal(FifoMoveQty * OldLayerCost, toValue);

                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                await AssertPlantGlMatchesLedgerAsync(db, ToPlant);

                // 廠際往來：兩廠一正一負，全公司合計恆為 0
                var fromGl = await GlNetByAccountAsync(db, FromPlant);
                var toGl   = await GlNetByAccountAsync(db, ToPlant);
                var clearing = StandardAccounts.InterPlantClearingCode;
                Assert.Equal(FifoMoveQty * OldLayerCost, Net(fromGl, clearing));
                Assert.Equal(-FifoMoveQty * OldLayerCost, Net(toGl, clearing));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditFifoLayerBalanceAsync(FromPlant));
                Assert.Empty(await audit.AuditFifoLayerBalanceAsync(ToPlant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(FromPlant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(ToPlant));
            }
        }

        /// <summary>
        /// 倒填單據：成本層的 <c>ReceiptDate</c> 必須是**單據上的異動日**，
        /// 不是建層那一天的系統時間。
        ///
        /// 修正前 <c>AddLayerAsync</c> 一律寫 <c>IDateTimeService.GetLocalDate()</c>，
        /// 於是全站所有成本層的日期都等於「過帳的那一天」。多數時候看不出來——
        /// 日期全相同，FIFO 的 <c>OrderBy(ReceiptDate).ThenBy(Id)</c> 就整條退化成
        /// 「依過帳順序」，答案剛好對。只有補單、倒填時兩者才會分岔：
        /// 使用者拿較早的異動日補一張進貨（日期欄是開放的 date input，只被期間鎖擋），
        /// 系統卻把它排在**已經在庫的較新批**後面，於是這一次領料領到錯的那一層，
        /// 發料成本、在製成本、乃至結轉的銷貨成本全部跟著錯，而數量完全正確、
        /// 傳票完全平衡、每一支稽核都不會叫。
        ///
        /// 這支從真實過帳入口（<c>PostLineDetailAsync</c>）進去，故意讓
        /// **建立順序與單據日期相反**：先收較新的那批、再補一張倒填的舊單。
        /// 只要日期沒有從 <c>MovementDate</c> 一路流到成本層，倒填那層就會排到後面，
        /// 跨廠調撥就會挑錯層、凍結成本就會變成 100 而不是 300。
        /// </summary>
        [Fact]
        public async Task BackdatedReceipt_TakesItsDocumentDate_AndIsConsumedFirst()
        {
            await SeedFifoMasterAsync();

            // 建立順序：新的先進系統，舊的後補（倒填）
            await ReceiveFifoLayerAsync("FLOW-XP-FLATE", StockDate.AddDays(10), NewLayerQty, NewLayerCost);
            await ReceiveFifoLayerAsync("FLOW-XP-FBACK", StockDate,             OldLayerQty, OldLayerCost);

            using (var scope = NewScope(out var db))
            {
                // ① 日期本身：兩層各自帶著自己單據的異動日，不是同一天（＝建層日）
                var layers = await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.PlantId == FromPlant && l.ProductCode == FifoMat)
                    .OrderBy(l => l.Id)
                    .Select(l => new { l.Id, l.SourceDocNo, l.ReceiptDate }).ToListAsync();
                Assert.Equal(2, layers.Count);
                Assert.Equal(StockDate.AddDays(10), layers[0].ReceiptDate);
                Assert.Equal(StockDate,             layers[1].ReceiptDate);

                // 倒填那層的 Id 比較大（後建），日期比較早——這正是新舊行為分岔的條件
                Assert.True(layers[1].Id > layers[0].Id);
                Assert.True(layers[1].ReceiptDate < layers[0].ReceiptDate);
            }

            // ② 行為：FIFO 依單據日取層，於是挑到後建的那一層
            await SeedTransferDocAsync(FifoDoc, FifoMat, FifoMoveQty);
            await ConfirmAsync(FromPlant, c => c.Confirm(FifoDoc));

            using (var scope = NewScope(out var db))
            {
                var xp = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == FifoDoc && m.TypeCode == "XP");
                Assert.Equal(OldLayerCost, xp.UnitCost);   // 舊行為會是 NewLayerCost（100）

                var remaining = await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.PlantId == FromPlant && l.ProductCode == FifoMat)
                    .OrderBy(l => l.Id)
                    .Select(l => l.QtyRemaining).ToListAsync();
                Assert.Equal(NewLayerQty, remaining[0]);                 // 較新那層一動也沒動
                Assert.Equal(OldLayerQty - FifoMoveQty, remaining[1]);   // 扣的是倒填那層

                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
            }
        }

        /// <summary>
        /// 4-10 中途反悔：確認 → 撤回 → 確認 → 撤回，每一圈都要乾淨地回到原點。
        ///
        /// 修正前作廢沖銷是「把該單所有異動逐筆反向」，而沖銷列自己也是同一張表的異動列，
        /// 於是第二圈會把第一圈的沖銷列也反轉一次：帳面總量因正負相消仍然正確
        /// （所以數量稽核與借貸平衡都不會叫），但明細帳上多出一組**從未發生**的出入庫，
        /// 沖銷筆數隨圈數平方成長，而且反轉那筆「+1 沖銷列」時要做可用量檢查——
        /// 貨若已被別的單領走，使用者只是想撤回自己剛剛的確認卻會收到「可用量不足」。
        ///
        /// 斷言鎖**筆數**而不只是總量：只驗總量的話，正負相消的錯誤寫法照樣全綠。
        /// </summary>
        [Fact]
        public async Task ConfirmThenCancel_TwoCycles_LeavesNoPhantomMovements()
        {
            await SeedFifoStockAsync();
            await SeedTransferDocAsync(FifoDoc, FifoMat, FifoMoveQty);

            var openQty = OldLayerQty + NewLayerQty;
            var openValue = OldLayerQty * OldLayerCost + NewLayerQty * NewLayerCost;

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                await ConfirmAsync(FromPlant, c => c.Confirm(FifoDoc));
                await ConfirmAsync(FromPlant, c => c.CancelConfirm(FifoDoc));

                using var scope = NewScope(out var db);

                var doc = await db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(d => d.CrossPlantTransferId == FifoDoc);
                Assert.Equal(DocStatus.Draft, doc.Status);

                Assert.Equal(openQty, await QtyOnHandAsync(db, FromPlant, FifoMat));

                var (qty, value) = await LayersAsync(FromPlant, FifoMat);
                Assert.Equal(openQty, qty);
                Assert.Equal(openValue, value);

                // 每一圈剛好兩筆：一筆撥出、一筆沖銷。逐筆反轉的寫法在第 2 圈會寫 3 筆，
                // 其中一組是憑空的「出庫 + 沖銷」。
                var moves = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == FifoDoc).ToListAsync();
                Assert.Equal(cycle * 2, moves.Count);
                Assert.Equal(0m, moves.Sum(m => m.StockQty * m.Direction));
                Assert.Equal(cycle, moves.Count(m => m.Direction == -1));
                Assert.Equal(cycle, moves.Count(m => m.Direction == 1));

                await AssertPlantGlMatchesLedgerAsync(db, FromPlant);
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditFifoLayerBalanceAsync(FromPlant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(FromPlant));
            }
        }

        /// <summary>
        /// 撤回要把量**還進當初被扣的那一層**，不是另建一層（W4-F14）。
        ///
        /// 修正前撤回走 <c>AddLayerAsync</c>：總量與總價值都會回到原點，
        /// 上面那支 ConfirmThenCancel 測的每一項都照樣通過，FIFO_LAYER_QTY_MISMATCH
        /// 也不會叫——但最舊的那批貨還回來以後 ReceiptDate 變成當天，排到最後面。
        /// 4-10 三圈實測：第 19 層 305 → 290，另外多出三個當天的 5@300 新層。
        /// 後果只在**下一次出庫**才顯形：本該取 300 的那批貨變成取 100，
        /// 銷貨成本從此偏掉，而且沒有任何既有稽核看得到。
        ///
        /// 斷言分兩段：層數不增（結構）、撤回後再出庫仍取最舊層的單價（行為）。
        /// 只驗總量會漏掉整個缺陷——那正是它躲過原本那支測試的方式。
        /// </summary>
        [Fact]
        public async Task CancelConfirm_OfFifoProduct_RestoresOriginalLayer_NotANewOne()
        {
            await SeedFifoStockAsync();
            await SeedTransferDocAsync(FifoDoc, FifoMat, FifoMoveQty);

            // 以「層 Id → 剩餘量」為快照。刻意不用 ReceiptDate 當識別：
            // AddLayerAsync 一律把 ReceiptDate 寫成**建層當天**（不是進貨日），
            // 所以同一天建的層日期全部相同，FIFO 實際順序落在 Id 上。
            async Task<Dictionary<int, decimal>> SnapshotLayersAsync()
            {
                using var s2 = NewScope(out var d2);
                return await d2.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.PlantId == FromPlant && l.ProductCode == FifoMat)
                    .OrderBy(l => l.Id)
                    .ToDictionaryAsync(l => l.Id, l => l.QtyRemaining);
            }

            var before = await SnapshotLayersAsync();

            await ConfirmAsync(FromPlant, c => c.Confirm(FifoDoc));
            await ConfirmAsync(FromPlant, c => c.CancelConfirm(FifoDoc));

            var after = await SnapshotLayersAsync();

            // ① 結構：同一組層 Id、同一組剩餘量。多出任何一個 Id 就代表走了「另建一層」。
            Assert.Equal(before, after);

            using (var scope = NewScope(out var db))
            {
                // ② 消耗紀錄已標記全數還回，不會被第二次撤回重複還量。
                var recs = await db.Set<InventoryCostLayerConsumption>().IgnoreQueryFilters().AsNoTracking()
                    .Where(c => c.DocNo == FifoDoc).ToListAsync();
                Assert.NotEmpty(recs);
                Assert.All(recs, r => Assert.Equal(r.Qty, r.RestoredQty));
            }

            // ③ 行為：撤回之後再確認一次，凍結成本仍是**最舊那層**的單價。
            //    若撤回是另建當天的新層，最舊的那批就排到最後，這裡會取到 NewLayerCost。
            await ConfirmAsync(FromPlant, c => c.Confirm(FifoDoc));

            using (var scope = NewScope(out var db))
            {
                var xp = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == FifoDoc && m.TypeCode == "XP" && m.Direction == -1)
                    .OrderByDescending(m => m.CreatedAt).FirstAsync();
                Assert.Equal(OldLayerCost, xp.UnitCost);
            }
        }

        /// <summary>
        /// 建單表單的**來源**儲位清單只能有本廠的（W4-F11）。
        ///
        /// StorageLocation 沒有 PlantId（主鍵是 (WhCode, LocationCode)），全域廠別過濾器
        /// 對它無效——控制器那段原本跟著倉庫寫成「全域 filter 已限定來源廠」，
        /// 但那句話只對 Warehouses 成立，儲位清單其實是全公司的。
        /// _Form 的 JS 新增明細列時又是從這份儲位清單反推來源倉下拉，
        /// 於是撥出端的倉庫選項會出現**別廠的倉**；選下去之後伺服端拿本廠去查
        /// 該儲位可用量，必然是 0，使用者只會看到一句莫名其妙的「可用量不足」。
        ///
        /// 斷言目的廠的倉不得出現在來源清單裡——那正是實測時漏進來的那一類。
        /// </summary>
        [Fact]
        public async Task CreateForm_SourceLocationOptions_AreScopedToSourcePlant()
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = FromPlant;
            var sp = new TestControllerServices(scope.ServiceProvider, db, FromPlant, RoleId);
            var ctrl = new CrossPlantTransferController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
            // Create() 非 AJAX 時走 RedirectToAction，那需要真的 UrlHelper。
            // 面板本來就是以 AJAX 取這支的 _Form，照它的真實用法呼叫。
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            await ctrl.Create();

            // ViewBag 裡是主組件的匿名型別，跨組件是 internal，dynamic 綁不到 → 走反射。
            var locations = ((System.Collections.IEnumerable)ctrl.ViewBag.SourceLocationOptions)
                .Cast<object>()
                .Select(o => (string)o.GetType().GetProperty("WhCode")!.GetValue(o)!)
                .ToList();

            Assert.Contains(Wh, locations);          // 本廠的倉要在
            Assert.DoesNotContain(ToWh, locations);  // 對面廠的倉不准在
        }

        /// <summary>
        /// 儲位查詢的廠別收斂機制本身（<c>StorageLocationQueryExtensions.ForCurrentPlant</c>）。
        ///
        /// 先把缺口釘成事實：<c>CurrentPlantId</c> 已設成來源廠，直接查
        /// <c>db.StorageLocations</c> 仍然**看得到對面廠的儲位**——因為 StorageLocation
        /// 沒有 PlantId 也沒實作 IPlantEntity，全域過濾器（<c>PlantId == CurrentPlantId</c>）
        /// 根本沒有掛到它身上。這是 W4-F11 那批 16 處缺陷共同的地基，不是某支控制器寫錯。
        ///
        /// 再驗修法：<c>ForCurrentPlant</c> 以 <c>db.Warehouses</c> 子查詢做存在性判定，
        /// 而 Warehouse 是 IPlantEntity → 廠別條件由過濾器提供。這裡順帶把
        /// 「EF 對子查詢裡的 DbSet 也會套用全域過濾器」這個前提測起來——整套修法都靠它，
        /// 若哪天 EF 版本改變行為，會在這裡紅，而不是在 16 支畫面上靜默跨廠。
        /// </summary>
        [Fact]
        public async Task ForCurrentPlant_KeepsOnlyCurrentPlantLocations()
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = FromPlant;

            // ① 缺口本身：沒有收斂時，對面廠的儲位照樣查得到。
            var unscoped = await db.StorageLocations
                .Select(l => l.LocationCode).ToListAsync();
            Assert.Contains(Loc, unscoped);
            Assert.Contains(ToLoc, unscoped);

            // ② 收斂後：只剩本廠的。
            var scoped = await db.StorageLocations.ForCurrentPlant(db)
                .Select(l => l.LocationCode).ToListAsync();
            Assert.Contains(Loc, scoped);
            Assert.DoesNotContain(ToLoc, scoped);

            // ③ 換廠即換清單——收斂確實跟著 CurrentPlantId 走，不是寫死來源廠。
            db.CurrentPlantId = ToPlant;
            var fromOtherSide = await db.StorageLocations.ForCurrentPlant(db)
                .Select(l => l.LocationCode).ToListAsync();
            Assert.Contains(ToLoc, fromOtherSide);
            Assert.DoesNotContain(Loc, fromOtherSide);
        }

        private Task SeedTransferDocAsync() => SeedTransferDocAsync(Doc, Mat, MoveQty);

        private async Task SeedTransferDocAsync(string docId, string product, decimal qty, string? lotNo = null)
        {
            using var scope = NewScope(out var db);
            var doc = new CrossPlantTransfer
            {
                CrossPlantTransferId = docId, TransferDate = TransferDate,
                PlantId = FromPlant, ToPlantId = ToPlant,
                FromWhCode = Wh, ToWhCode = ToWh,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, CreatedByRoleId = RoleId,
            };
            doc.Lines.Add(new CrossPlantTransferLine
            {
                CrossPlantTransferId = docId, LineNo = 1, ProductCode = product,
                Qty = qty, StockQty = qty, UomCode = Uom, StockUomCode = Uom,
                FromWhCode = Wh, FromLocationCode = Loc,
                ToWhCode = ToWh, ToLocationCode = ToLoc, LotNo = lotNo,
            });
            db.CrossPlantTransfers.Add(doc);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 走真正的控制器入口。撥出只有來源廠做得了、收貨只有目的廠做得了，
        /// 兩段的使用者廠別上下文不同，這正是跨廠調撥與其他單據不一樣的地方。
        /// </summary>
        private async Task ConfirmAsync(string actingPlant, Func<CrossPlantTransferController, Task<IActionResult>> action)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = actingPlant;
            var sp = new TestControllerServices(scope.ServiceProvider, db, actingPlant, RoleId);
            var ctrl = new CrossPlantTransferController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };

            var result = await action(ctrl);
            if (result is BadRequestObjectResult bad)
            {
                var msg = bad.Value?.GetType().GetProperty("message")?.GetValue(bad.Value);
                Assert.Fail($"跨廠調撥動作應成功（{actingPlant}）：{msg}");
            }
            Assert.IsType<OkObjectResult>(result);
        }
    }
}
