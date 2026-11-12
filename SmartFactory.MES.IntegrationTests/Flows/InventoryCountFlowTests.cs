using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-7：盤點的盤盈／盤虧／零差異三態。
    ///
    /// 作業書原本只寫「差異調整正確產生庫存異動與傳票」。這條看起來像例行確認，
    /// 但「傳票正確」在盤點這件事上有一個明確的、與進貨完全不同的語意：
    /// 盤盈不是進貨。盤點多出來的貨沒有供應商、沒有人會來請款，
    /// 把它貸進「進貨暫估應付」等於在負債科目裡憑空長出一筆永遠沒人來沖的錢，
    /// 而且 AP 那端根本不知道它存在——每盤一次就多掛一筆，永不收斂。
    ///
    /// 因此本測試斷言的是三件事：
    ///   ① 三態都能真的走完（結帳不得失敗）
    ///   ② 每一態之後，該廠存貨科目淨額 == 該廠庫存明細帳合計
    ///   ③ 盤點結帳不得動到暫估應付（2152）——那是採購鏈專屬的科目
    /// </summary>
    [Collection("Integration")]
    public class InventoryCountFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW4C";

        private const string MatUp   = "FLOW-W4C-UP";    // 盤盈的料
        private const string MatDown = "FLOW-W4C-DOWN";  // 盤虧的料
        private const string MatFlat = "FLOW-W4C-FLAT";  // 零差異的料

        private static readonly DateOnly StockDate = new(2098, 6, 1);

        private const decimal OpenQty = 100m;
        private const decimal CostUp = 40m, CostDown = 25m, CostFlat = 12m;
        private const decimal Surplus = 10m, Shortage = 10m;

        public InventoryCountFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync(
                (MatUp, "盤盈測試料"), (MatDown, "盤虧測試料"), (MatFlat, "零差異測試料"));

            using var scope = NewScope(out var db);
            // 盤點清冊與盤盈虧調整單不在 FlowTestBase 的清場範圍內，各自補。
            var sheetIds = await db.InventoryCountSheets.IgnoreQueryFilters()
                .Where(s => s.PlantId == Plant).Select(s => s.SheetId).ToListAsync();
            if (sheetIds.Count > 0)
            {
                await db.InventoryCountItems.IgnoreQueryFilters()
                    .Where(i => sheetIds.Contains(i.SheetId)).ExecuteDeleteAsync();
                await db.InventoryCountSheets.IgnoreQueryFilters()
                    .Where(s => s.PlantId == Plant).ExecuteDeleteAsync();
            }
            await db.StockAdjustments.IgnoreQueryFilters()
                .Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
        }

        /// <summary>
        /// 盤盈與盤虧同時發生（真實盤點一定是混著的）。
        /// 結帳之後庫存要對、總帳要對，而且暫估應付不得被碰到。
        /// </summary>
        [Fact]
        public async Task ClosingCount_WithSurplusAndShortage_PostsAdjustmentWithoutTouchingGrIr()
        {
            await SeedOpeningStockAsync();

            decimal grIrBefore;
            using (var scope = NewScope(out var db))
                grIrBefore = Net(await GlNetByAccountAsync(db, Plant), StandardAccounts.GrIrAPClearingCode);

            var sheetId = await CreateSheetAsync();
            await EnterCountsAsync(sheetId, new Dictionary<string, decimal>
            {
                [MatUp]   = OpenQty + Surplus,
                [MatDown] = OpenQty - Shortage,
                [MatFlat] = OpenQty,
            });
            await CloseAsync(sheetId);

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(OpenQty + Surplus,  await QtyOnHandAsync(db, Plant, MatUp));
                Assert.Equal(OpenQty - Shortage, await QtyOnHandAsync(db, Plant, MatDown));
                Assert.Equal(OpenQty,            await QtyOnHandAsync(db, Plant, MatFlat));

                // 盤盈的單價不得被稀釋：多出來的貨用的就是現行單位成本
                var upInv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == MatUp);
                Assert.Equal(CostUp, upInv.UnitCost);

                await AssertGlMatchesLedgerAsync(db);
                await AssertEveryEntryBalancedAsync(db, Plant);

                // 盤點不是採購：暫估應付必須原封不動
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(grIrBefore, Net(gl, StandardAccounts.GrIrAPClearingCode));

                // 盤盈進其他收入、盤虧進損失，各自對得上差異金額
                Assert.Equal(-(Surplus * CostUp), Net(gl, StandardAccounts.InventoryGainCode));
                Assert.Equal(Shortage * CostDown, Net(gl, StandardAccounts.InventoryLossCode));

                // 零差異的料不得產生任何庫存異動
                var flatMoves = await db.StockMovements.IgnoreQueryFilters()
                    .CountAsync(m => m.PlantId == Plant && m.ProductCode == MatFlat && m.TypeCode == "IC");
                Assert.Equal(0, flatMoves);

                // 調整單已過帳就要是 Posted：原本停在 Confirmed（待倉庫作業），庫存已動、單據卻作廢不了
                var sheet = await db.InventoryCountSheets.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(s => s.SheetId == sheetId);
                var adjStatus = await db.StockAdjustments.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.StockAdjustmentId == sheet.AdjustmentDocId).Select(a => a.Status).SingleAsync();
                Assert.Equal(Modules.INV.Models.DocStatus.Posted, adjStatus);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }

        /// <summary>
        /// 全部零差異：結帳仍要成功，且不得產生任何庫存異動或傳票。
        /// 「什麼都沒發生」也是一種必須被驗證的結果——盤點是每期都會做的例行動作，
        /// 若零差異結帳會失敗或會產生空調整單，帳上就會被例行動作汙染。
        /// </summary>
        [Fact]
        public async Task ClosingCount_WithNoVarianceAtAll_LeavesNoMovementAndNoJournal()
        {
            await SeedOpeningStockAsync();

            int movesBefore, journalsBefore;
            using (var scope = NewScope(out var db))
            {
                movesBefore    = await db.StockMovements.IgnoreQueryFilters().CountAsync(m => m.PlantId == Plant);
                journalsBefore = await db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.PlantId == Plant);
            }

            var sheetId = await CreateSheetAsync();
            await EnterCountsAsync(sheetId, new Dictionary<string, decimal>
            {
                [MatUp] = OpenQty, [MatDown] = OpenQty, [MatFlat] = OpenQty,
            });
            await CloseAsync(sheetId);

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(movesBefore,
                    await db.StockMovements.IgnoreQueryFilters().CountAsync(m => m.PlantId == Plant));
                Assert.Equal(journalsBefore,
                    await db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.PlantId == Plant));

                var sheet = await db.InventoryCountSheets.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(s => s.SheetId == sheetId);
                Assert.Equal("Closed", sheet.SheetStatus);

                await AssertGlMatchesLedgerAsync(db);
            }
        }

        /// <summary>
        /// FIFO 品號盤盈：結帳必須成功，且入庫成本取**現存成本層的加權平均**。
        ///
        /// 原本盤點結帳建調整單時完全不帶 UnitPrice，而 FIFO 入庫沒帶單價會被
        /// <c>InventoryPostingService</c> 的守門擋下（W2-F8 補的，用來擋退料漏帶凍結成本），
        /// 於是整張清冊只要盤出一筆 FIFO 盤盈就**結不了帳**，錯誤訊息還是
        /// 「入庫未帶單價，無法建立 FIFO 成本層」——離「盤點」這件事非常遠。
        ///
        /// 為什麼既有的盤點流程測試看不到：<c>FlowTestBase</c> 把測試用的產品類別
        /// 一律建成 MovingAverage，**所有** Flows 測試都跑不到 FIFO 分支。
        /// 所以這一支自己建 FIFO 類別與品號。
        /// </summary>
        [Fact]
        public async Task ClosingCount_SurplusOnFifoProduct_PricesAtLayerWeightedAverage()
        {
            const string fifoCat  = "FLOW-W4C-FIFOCAT";
            const string fifoProd = "FLOW-W4C-FIFO";
            const decimal layerCost = 30m;

            await SeedFifoProductAsync(fifoCat, fifoProd, layerCost);

            var sheetId = await CreateSheetAsync(expectedItems: 4);
            await EnterCountsAsync(sheetId, new Dictionary<string, decimal>
            {
                [MatUp] = OpenQty, [MatDown] = OpenQty, [MatFlat] = OpenQty,
                [fifoProd] = OpenQty + 1m,          // 盤盈 1
            });
            await CloseAsync(sheetId);

            using var scope = NewScope(out var db);
            var sheet = await db.InventoryCountSheets.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(s => s.SheetId == sheetId);
            Assert.Equal("Closed", sheet.SheetStatus);
            Assert.NotNull(sheet.AdjustmentDocId);

            var line = await db.StockAdjustmentLines.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.StockAdjustmentId == sheet.AdjustmentDocId && l.ProductCode == fifoProd);
            Assert.Equal(1m, line.Qty);
            Assert.Equal(1, line.Direction);
            Assert.Equal(layerCost, line.UnitPrice);          // 不帶單價時整張清冊會結不了帳

            var mv = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(m => m.DocNo == sheet.AdjustmentDocId && m.ProductCode == fifoProd);
            Assert.Equal(layerCost, mv.UnitCost);

            // 成本層總量必須跟著在手量走——盤盈要建出新的一層，不是只加數量。
            var layerQty = await db.Set<SmartFactory.MES.Modules.INV.Models.InventoryCostLayer>()
                .IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PlantId == Plant && l.ProductCode == fifoProd)
                .SumAsync(l => l.QtyRemaining);
            var onHand = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == fifoProd)
                .SumAsync(i => i.QtyOnHand);
            Assert.Equal(OpenQty + 1m, onHand);
            Assert.Equal(onHand, layerQty);
        }

        /// <summary>
        /// 零差異不得留下空的草稿調整單。
        ///
        /// 原本無條件先配單號、先建單再說，於是盤點最常見（也最該安靜）的結果，
        /// 每次都留下一張 0 明細的草稿調整單：白佔一個單號，還會出現在庫存調整
        /// 列表裡等著被人誤點確認。
        /// </summary>
        [Fact]
        public async Task ClosingCount_WithNoVarianceAtAll_CreatesNoAdjustmentDocument()
        {
            await SeedOpeningStockAsync();

            var sheetId = await CreateSheetAsync();
            await EnterCountsAsync(sheetId, new Dictionary<string, decimal>
            {
                [MatUp] = OpenQty, [MatDown] = OpenQty, [MatFlat] = OpenQty,
            });
            await CloseAsync(sheetId);

            using var scope = NewScope(out var db);
            var sheet = await db.InventoryCountSheets.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(s => s.SheetId == sheetId);
            Assert.Equal("Closed", sheet.SheetStatus);
            Assert.Null(sheet.AdjustmentDocId);
            Assert.Empty(await db.StockAdjustments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == Plant).ToListAsync());
        }

        /// <summary>
        /// 「FIFO 成本層總量 ＝ 在手量」這個不變量歪掉時，只有新稽核看得見。
        ///
        /// 既有的兩道防線一致地沉默：<c>INV_ROLLFORWARD</c> 是數量型稽核而
        /// <c>Inventories.QtyOnHand</c> 完全正確（成本層根本不在它的視野裡），
        /// 傳票也張張平衡——錯的是「之後出庫要用哪個單價」，不是「有沒有記帳」。
        /// </summary>
        [Fact]
        public async Task FifoLayerQtyDivergingFromOnHand_IsOnlyVisibleToNewAudit()
        {
            ExpectInvariantViolation("FIFO_LAYER_QTY_MISMATCH", "本測試就是故意讓成本層與在手量分歧，驗證稽核抓得到");
            ExpectInvariantViolation("INV_GL_SUBLEDGER", "多塞的那一層沒有傳票，明細帳價值因此比總帳多 300——同一個故意製造的錯在金額面的樣子");
            const string fifoCat  = "FLOW-W4C-FIFOCAT";
            const string fifoProd = "FLOW-W4C-FIFO";

            await SeedFifoProductAsync(fifoCat, fifoProd, unitCost: 30m);

            using (var scope = NewScope(out var db))
            {
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditFifoLayerBalanceAsync(Plant));   // 前置：本來是平的

                // 重現「作廢只沖數量、不沖成本」（W3-F2）留下的形狀：
                // 數量與庫存異動都被正確沖掉了，**只有成本層留在原地**。
                // 所以這裡是「多一層」而不是「減在手量」——後者會連帶破壞
                // 庫存捲動不變量，那樣 INV_ROLLFORWARD 就會叫，測不到「只有新稽核看得見」。
                db.Set<SmartFactory.MES.Modules.INV.Models.InventoryCostLayer>().Add(
                    new SmartFactory.MES.Modules.INV.Models.InventoryCostLayer
                    {
                        PlantId = Plant, ProductCode = fifoProd, LocationCode = Loc, LotNo = "",
                        ReceiptDate = StockDate, SourceDocNo = "FLOW-W4C-ORPHAN",
                        QtyRemaining = 10m, UnitCost = 30m,
                    });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var audit = Svc<IDataAuditService>(scope);

                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));

                var f = Assert.Single(await audit.AuditFifoLayerBalanceAsync(Plant));
                Assert.Equal("FIFO_LAYER_QTY_MISMATCH", f.AuditType);
                Assert.Equal(fifoProd, f.ProductCode);
                Assert.Equal(-10m * 30m, f.DifferenceAmount);   // 在手 − 層 ＝ −10，× 均價 30
            }
        }

        private async Task SeedFifoProductAsync(string categoryCode, string productCode, decimal unitCost)
        {
            using (var scope = NewScope(out var db))
            {
                if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == categoryCode))
                    db.ProductCategories.Add(new SmartFactory.MES.Modules.BAS.Models.ProductCategory
                    {
                        CategoryCode = categoryCode, CategoryName = "FIFO 盤點測試類別",
                        CostMethod = SmartFactory.MES.Modules.BAS.Models.CostMethod.Fifo, IsActive = true,
                    });
                if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == productCode))
                    db.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
                    {
                        ProductCode = productCode, ProductName = "FIFO 盤點測試料",
                        CategoryCode = categoryCode,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });
                await db.SaveChangesAsync();
            }

            await SeedOpeningStockAsync();

            using (var scope = NewScope(out var db))
            {
                var posting = Svc<IInventoryPostingService>(scope);
                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-W4C-FIFOOPEN", LineNo = 1,
                    ProductCode = productCode, LocationCode = Loc, LotNo = "",
                    Qty = OpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = unitCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
        }

        // ── 斷言輔助 ────────────────────────────────────────────────────────

        private async Task AssertGlMatchesLedgerAsync(ApplicationDbContext db)
        {
            var gl = await GlNetByAccountAsync(db, Plant);
            var ledger = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant)
                .SumAsync(i => (decimal?)(i.QtyOnHand * i.UnitCost)) ?? 0m;

            Assert.Equal(
                Math.Round(ledger, 2),
                Math.Round(Net(gl, StandardAccounts.RawMaterialInventoryCode), 2));
        }

        // ── 佈景與控制器驅動 ────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync()
        {
            using var scope = NewScope(out var db);
            var posting = Svc<IInventoryPostingService>(scope);
            var seeds = new (string Product, decimal Cost)[]
            {
                (MatUp, CostUp), (MatDown, CostDown), (MatFlat, CostFlat),
            };
            var lineNo = 0;
            foreach (var (product, cost) in seeds)
            {
                lineNo++;
                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-W4C-OPEN", LineNo = lineNo,
                    ProductCode = product, LocationCode = Loc, LotNo = "",
                    Qty = OpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<string> CreateSheetAsync(int expectedItems = 3)
        {
            var sheet = new InventoryCountSheet
            {
                CountMode = "WithSheet",
                WhCodeFrom = Wh, WhCodeTo = Wh,
                Remarks = "Wave 4-7 盤點流程測試",
            };
            var json = await DriveAsync(c => c.Create(sheet));
            var data = json.Value?.GetType().GetProperty("data")?.GetValue(json.Value);
            var sheetId = data?.GetType().GetProperty("sheetId")?.GetValue(data) as string;
            Assert.False(string.IsNullOrEmpty(sheetId), "建立盤點清冊應回傳單號");

            using var scope = NewScope(out var db);
            var items = await db.InventoryCountItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SheetId == sheetId).ToListAsync();
            Assert.Equal(expectedItems, items.Count);
            Assert.All(items, i => Assert.Equal(OpenQty, i.BookQty));
            return sheetId!;
        }

        private async Task EnterCountsAsync(string sheetId, Dictionary<string, decimal> actualByProduct)
        {
            List<(int ItemId, string Product)> items;
            using (var scope = NewScope(out var db))
                items = await db.InventoryCountItems.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.SheetId == sheetId)
                    .Select(i => new ValueTuple<int, string>(i.ItemId, i.ProductCode))
                    .ToListAsync();

            foreach (var (itemId, product) in items)
                await DriveAsync(c => c.SaveFirstCount(itemId, actualByProduct[product], null));
        }

        private async Task CloseAsync(string sheetId) => await DriveAsync(c => c.Close(sheetId));

        /// <summary>走真正的控制器入口；任何 JsonFail 都直接讓測試失敗並印出訊息。</summary>
        private async Task<JsonResult> DriveAsync(
            Func<InventoryCountController, Task<IActionResult>> action)
        {
            using var scope = NewScope(out var db);
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            var ctrl = new InventoryCountController(
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IExportService>(),
                sp.GetRequiredService<IInventoryCostLayerService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };

            var json = Assert.IsType<JsonResult>(await action(ctrl));
            var success = json.Value?.GetType().GetProperty("success")?.GetValue(json.Value) as bool?;
            if (success != true)
            {
                var msg = json.Value?.GetType().GetProperty("message")?.GetValue(json.Value);
                Assert.Fail($"盤點動作應成功：{msg}");
            }
            return json;
        }
    }
}
