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
    /// 同廠移倉（備料、IQC 上架、一般移倉都走 <c>PostSamePlantTransferAsync</c>）
    /// 的 FIFO 成本層必須**跟著貨換儲位**。
    ///
    /// 成本層與 <c>ConsumeLayersAsync</c> 的鍵都是 (廠, 品號, **儲位**, 批)。
    /// 移倉若只搬數量不搬層，兩個儲位一多一少剛好相消，
    /// **廠級總量完全相等**、在手量從頭到尾正確、傳票也沒有半張不平
    /// （同廠移倉本來就不產生分錄），於是每一道既有防線都沉默；
    /// 直到有人從新儲位出料，才會撞上一句「FIFO 成本層不足以扣除」——
    /// 現場看到的是一個與操作完全對不上的錯誤，而真正的原因發生在幾週前的那張移倉單。
    ///
    /// 2026-09-07 在 dev 庫實測到這個形狀的殘留：備料單 IM-2607-00001 把
    /// MAF3106／MAK3106 各 441 個從 W001-1 移到 STG-01，層全部留在 W001-1。
    /// 那批資料是修正前產生的，程式面已由 W4-F9 補上；這支測試把它釘住。
    /// </summary>
    [Collection("Integration")]
    public class SamePlantTransferFifoFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZTRF";

        private const string Cat  = "FLOW-TR-FIFOCAT";
        private const string Mat  = "FLOW-TR-FMAT";
        private const string Doc  = "FLOW-TR-01";

        private static readonly DateOnly StockDate    = new(2098, 6, 1);
        private static readonly DateOnly TransferDate = new(2098, 6, 20);

        // 兩層不同單價、先進的比較貴：分得出「取被消耗那幾層的加權均價」與
        // 「取現有層的加權均價」——後者是 (40×300 + 60×100) / 100 = 180，不是 260。
        private const decimal OldQty = 40m, OldCost = 300m;
        private const decimal NewQty = 60m, NewCost = 100m;
        private const decimal MoveQty = 50m;

        /// <summary>被搬走那 50 個的加權均價：40 個 @300 ＋ 10 個 @100。</summary>
        private const decimal MovedUnitCost = 260m;

        private string Loc2 => $"{Plant}-LOC2";

        public SamePlantTransferFifoFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            // 刻意不把 Mat 交給 ResetAsync：基底會用共用的 FLOW-CAT（移動均價）建品號，
            // 之後 SeedFifoStockAsync 的 if-not-exists 就補不上 FIFO 類別，
            // 成本層一層都不會建，而在手量完全正確——測試會紅在一個看起來毫不相干的地方。
            await ResetAsync();
            using var scope = NewScope(out var db);
            await db.StockTransfers.IgnoreQueryFilters()
                .Where(d => d.PlantId == Plant).ExecuteDeleteAsync();

            // 基底只建一個儲位，移倉得有第二個。
            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == Wh && l.LocationCode == Loc2))
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = Wh, LocationCode = Loc2, LocationName = "同廠移倉目的儲位",
                });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 移倉之後：來源儲位的層被扣掉、目的儲位長出同價值的一層，
        /// 而且**從目的儲位出料真的出得掉**——後者才是這個不變量存在的理由。
        /// </summary>
        [Fact]
        public async Task Transfer_MovesCostLayersToDestinationLocation_SoIssuingFromThereWorks()
        {
            await SeedFifoStockAsync();
            await SeedTransferDocAsync();

            await PostTransferAsync();

            using (var scope = NewScope(out var db))
            {
                var layers = await LayersAsync(db);

                // 來源儲位：先進的 40 @300 整層被吃掉（層本身留著、餘量歸零），貴的那層先走
                var from = layers.Where(l => l.LocationCode == Loc).ToList();
                Assert.Equal(NewQty - (MoveQty - OldQty), from.Sum(l => l.QtyRemaining));   // 60 − 10 = 50
                Assert.Equal(0m, from.Single(l => l.UnitCost == OldCost).QtyRemaining);
                Assert.All(from.Where(l => l.QtyRemaining > 0m), l => Assert.Equal(NewCost, l.UnitCost));

                // 目的儲位：一層，數量與價值都等於被搬走的那批
                var to = Assert.Single(layers.Where(l => l.LocationCode == Loc2));
                Assert.Equal(MoveQty, to.QtyRemaining);
                Assert.Equal(MovedUnitCost, to.UnitCost);
                Assert.Equal(TransferDate, to.ReceiptDate);   // 層齡跟著單據日，不是建層當天

                // 全廠價值不因換儲位而改變
                Assert.Equal(OldQty * OldCost + NewQty * NewCost,
                             layers.Sum(l => l.QtyRemaining * l.UnitCost));

                Assert.Empty(await Svc<IDataAuditService>(scope).AuditFifoLayerBalanceAsync(Plant));
            }

            // 從目的儲位把整批出掉：修正前這一步會丟「FIFO 成本層不足以扣除」
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-TR-ISSUE", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc2, LotNo = "",
                    // 報廢而非生產領料：IO 屬於工單、不入總帳，不掛工單的 IO 會讓總帳存貨對明細帳永遠差這一筆（INV_GL_SUBLEDGER）
                    Qty = MoveQty, TypeCode = "IJ", TypeCategory = "Scrap",
                    MovementDate = TransferDate, UomCode = Uom, StockUomCode = Uom,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                // 發料成本取的是被搬過來那層的單價，不是原儲位剩下的 100
                var issue = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == "FLOW-TR-ISSUE");
                Assert.Equal(MovedUnitCost, issue.UnitCost);

                Assert.Equal(0m, await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Mat && i.LocationCode == Loc2)
                    .SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m);
                Assert.Empty((await LayersAsync(db)).Where(l => l.LocationCode == Loc2 && l.QtyRemaining > 0));
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditFifoLayerBalanceAsync(Plant));
            }
        }

        /// <summary>
        /// 守門本身：層留在原儲位時，稽核必須叫。
        ///
        /// 這支刻意把資料弄成「數量搬了、層沒搬」的形狀——那正是修正前
        /// 備料單留下的形狀。重點在於**廠級總量兩邊都完全相等**：
        /// 稽核原本就是廠級彙總（放寬的理由正是這個缺陷本身），於是它一聲不吭。
        /// 改成逐儲位比對之後，一多一少會各報一筆。
        /// </summary>
        [Fact]
        public async Task LayersLeftBehindInSourceLocation_AreCaughtEvenThoughPlantTotalsMatch()
        {
            ExpectInvariantViolation("FIFO_LAYER_QTY_MISMATCH", "本測試故意把成本層留在來源儲位，驗證全廠合計相同時稽核仍抓得到");
            await SeedFifoStockAsync();
            await SeedTransferDocAsync();
            await PostTransferAsync();

            using (var scope = NewScope(out var db))
            {
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditFifoLayerBalanceAsync(Plant));

                // 把成本層倒回修正前的樣子：目的儲位那層不存在，來源儲位的層原封不動。
                // 刻意保留移倉真的寫下的 WP/WI 兩筆庫存異動與在手量——少了它們，
                // 破壞的就變成庫存捲動不變量，INV_ROLLFORWARD 會先叫，
                // 那就測不到「只有這一支稽核看得見」。
                db.InventoryCostLayers.RemoveRange(
                    await db.InventoryCostLayers.IgnoreQueryFilters()
                        .Where(l => l.ProductCode == Mat && l.LocationCode == Loc2).ToListAsync());
                foreach (var l in await db.InventoryCostLayers.IgnoreQueryFilters()
                             .Where(l => l.ProductCode == Mat && l.LocationCode == Loc).ToListAsync())
                    l.QtyRemaining = l.UnitCost == OldCost ? OldQty : NewQty;
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                // 廠級：在手量 100、層 100，完全相等——舊的廠級彙總看不到任何差異
                var totalOnHand = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Mat).SumAsync(i => i.QtyOnHand);
                var totalLayers = (await LayersAsync(db)).Sum(l => l.QtyRemaining);
                Assert.Equal(totalOnHand, totalLayers);

                var audit = Svc<IDataAuditService>(scope);

                // 數量型稽核照樣全綠：錯的不是「有沒有記帳」，是「之後要扣哪一層」
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));

                var failures = await audit.AuditFifoLayerBalanceAsync(Plant);
                Assert.Equal(2, failures.Count);
                Assert.All(failures, f =>
                {
                    Assert.Equal("FIFO_LAYER_QTY_MISMATCH", f.AuditType);
                    Assert.Equal(Mat, f.ProductCode);
                    Assert.True(f.IsBlocking);   // 這是本系統自己的帳歪了，不是對外介面狀態
                });

                // 來源儲位層多（在手 50 − 層 100 ＝ −50）、目的儲位層少（50 − 0 ＝ +50）
                Assert.Contains(failures, f => f.DocId == $"{Mat}@{Loc}"  && f.DifferenceAmount < 0m);
                Assert.Contains(failures, f => f.DocId == $"{Mat}@{Loc2}" && f.DifferenceAmount == 0m);
            }
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedFifoStockAsync()
        {
            using (var scope = NewScope(out var db))
            {
                if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                    db.ProductCategories.Add(new ProductCategory
                    {
                        CategoryCode = Cat, CategoryName = "同廠移倉 FIFO 類別",
                        CostMethod = CostMethod.Fifo, IsActive = true,
                    });
                var product = await db.Products.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.ProductCode == Mat);
                if (product == null)
                    db.Products.Add(new Product
                    {
                        ProductCode = Mat, ProductName = "同廠移倉 FIFO 料", CategoryCode = Cat,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });
                else
                    // 共用測試庫，品號是全域主檔：舊殘留可能掛在別的類別上，計價方式就會不對。
                    product.CategoryCode = Cat;
                await db.SaveChangesAsync();
            }

            await ReceiveAsync("FLOW-TR-OPEN1", StockDate,             OldQty, OldCost);
            await ReceiveAsync("FLOW-TR-OPEN2", StockDate.AddDays(10), NewQty, NewCost);
        }

        private async Task ReceiveAsync(string docNo, DateOnly date, decimal qty, decimal cost)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = date, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedTransferDocAsync()
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
                Qty = MoveQty, StockQty = MoveQty, UomCode = Uom, StockUomCode = Uom,
                FromWhCode = Wh, FromLocationCode = Loc,
                ToWhCode = Wh, ToLocationCode = Loc2,
            });
            db.StockTransfers.Add(doc);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 走過帳服務而非控制器：同廠移倉的控制器只是權限＋狀態守門，
        /// 本測試要問的是成本層，走服務可以避開一整組與本題無關的 HTTP 佈景。
        /// 控制器那一層（Confirm → PostTransferCoreAsync）另有殘留守門測試涵蓋。
        /// </summary>
        private async Task PostTransferAsync()
        {
            using var scope = NewScope(out var db);
            var doc = await db.StockTransfers.Include(d => d.Lines)
                .SingleAsync(d => d.StockTransferId == Doc);
            await Svc<IInventoryPostingService>(scope).PostSamePlantTransferAsync(
                new PostSamePlantTransferRequest
                {
                    PlantId = Plant, DocNo = Doc, MovementDate = doc.TransferDate, CreatedBy = UserId,
                    Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new TransferLine
                    {
                        LineNo = l.LineNo, ProductCode = l.ProductCode,
                        FromLocationCode = l.FromLocationCode ?? "",
                        ToLocationCode = l.ToLocationCode ?? "",
                        StockQty = l.StockQty, UomCode = l.UomCode ?? "", StockUomCode = l.StockUomCode ?? "",
                        SetQtyPutAway = q => { l.QtyPutAway = q; },
                    }).ToList(),
                    SetDocPosted = () => { doc.Status = DocStatus.Posted; },
                });
            await db.SaveChangesAsync();
        }

        private static async Task<List<InventoryCostLayer>> LayersAsync(Data.ApplicationDbContext db)
            => await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.ProductCode == Mat).OrderBy(l => l.Id).ToListAsync();
    }
}
