using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 盤點清冊（InventoryCountSheet）的刪除 × 過帳殘留守門。
    ///
    /// 這張單不能套 <see cref="InvDocResidueTestBase"/>，因為它的過帳足跡**不掛在自己的單號上**：
    /// 結帳時另建一張庫存調整單（單號由採番取得），差異異動全部記在那張調整單底下，
    /// 清冊與調整單之間只靠 <c>Remarks = "盤點結帳 {SheetId}"</c> 這條字串線索相連。
    /// 換句話說它是全庫關聯最弱的一組過帳——清冊被刪掉的話，留下的調整單連
    /// 「這是誰產生的」都查不到，比一般孤兒異動更糟。
    ///
    /// 也因此本檔最重要的兩條不是刪除守門，而是重複結帳那一組：
    /// <see cref="CloseTwice_DoesNotCreateSecondAdjustment"/> 守狀態守門本身，
    /// <see cref="CloseReplay_AfterStatusRevertedToCounting_IsStillBlocked"/> 守
    /// 「狀態守門被繞過之後還剩什麼」——原本什麼都不剩（第二次會拿到新的調整單號，
    /// 而過帳層的冪等是以單號為鍵的），現在剩 <c>AdjustmentDocId</c> 這個資料上的事實。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class InventoryCountPostingResidueTests : PostingResidueTestBase
    {
        private const string Route     = "/InventoryCount";
        private const string TypeCode  = "IC";      // Close 硬寫，不可改用測試前綴
        private const string Product   = Prefix + "-CP01";
        private const string Category  = Prefix + "-CCAT";
        private const string Uom       = Prefix + "-CEA";
        private const string Wh        = Prefix + "-CWH";
        private const string Loc       = Prefix + "-CLOC";

        private const decimal BookQty  = 100m;
        private const decimal CountQty = 90m;       // 盤虧 10，差異必為負向

        public InventoryCountPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 已結帳的清冊不可刪除，且結帳產生的調整單與庫存異動不得有任何變動。
        /// </summary>
        [Fact]
        public async Task ClosedSheet_Delete_Blocked_AndAdjustmentUntouched()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(ClosedSheet_Delete_Blocked_AndAdjustmentUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Close/{sheetId}"), "結帳");

            var (adjId, posted) = await CaptureCloseFootprintAsync(sheetId);
            Assert.NotNull(adjId);
            Assert.True(posted.MovementLines > 0, $"前置條件不成立：結帳應產生差異異動，實際 {posted}");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{sheetId}"),
                "刪除已結帳的盤點清冊");

            await AssertUnchangedAsync(posted, adjId!, "刪除被擋後");
            Assert.True(await SheetExistsAsync(sheetId), "刪除被擋下了，但清冊卻不見了。");
        }

        /// <summary>
        /// 重複結帳必須被狀態守門擋下。
        /// </summary>
        [Fact]
        public async Task CloseTwice_DoesNotCreateSecondAdjustment()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(CloseTwice_DoesNotCreateSecondAdjustment));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Close/{sheetId}"), "第一次結帳");

            var adjCount = await CountAdjustmentsAsync(sheetId);
            Assert.Equal(1, adjCount);
            var onHandAfterFirst = await ReadOnHandAsync();

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Close/{sheetId}"), "第二次結帳");

            Assert.Equal(1, await CountAdjustmentsAsync(sheetId));
            Assert.Equal(onHandAfterFirst, await ReadOnHandAsync());
        }

        /// <summary>
        /// 狀態被改回 Counting 之後重放結帳，仍不得產生第二張調整單。
        ///
        /// 上一格測的是狀態守門本身；這一格測的是**守門失效時還剩什麼**。
        /// 原本什麼都不剩：<c>PostDocSimplifiedAsync</c> 的冪等檢查以「單號 + 行號 + 異動類別」
        /// 為鍵，而第二次結帳會另外採番出一張新的調整單——等於換了一把鑰匙，
        /// 那道防線完全攔不到，同一批差異被扣兩次庫存，兩張調整單看起來都合法。
        ///
        /// 把狀態直接改回 Counting 不是在模擬使用者操作，而是在模擬
        /// 「守門被繞過」這件事本身（人為改庫、日後加上的重開功能、或狀態機出錯）。
        /// 現在擋下它的是 <c>AdjustmentDocId</c>：一張清冊終身只對一張調整單，
        /// 這是資料上的事實，不是狀態這種可以被改回去的東西。
        /// </summary>
        [Fact]
        public async Task CloseReplay_AfterStatusRevertedToCounting_IsStillBlocked()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(CloseReplay_AfterStatusRevertedToCounting_IsStillBlocked));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Close/{sheetId}"), "第一次結帳");

            var firstAdjId       = await ReadAdjustmentDocIdAsync(sheetId);
            var onHandAfterFirst = await ReadOnHandAsync();
            Assert.False(string.IsNullOrEmpty(firstAdjId),
                "前置條件不成立：結帳應把調整單號寫回清冊，否則本測試守的東西根本不存在。");

            // 繞過狀態守門：只把狀態改回去，其餘欄位（含 AdjustmentDocId）保持結帳後的樣子
            await ResetSheetStatusToCountingAsync(sheetId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Close/{sheetId}"), "狀態被改回後重放結帳");

            Assert.Equal(1, await CountAdjustmentsAsync(sheetId));
            Assert.Equal(firstAdjId, await ReadAdjustmentDocIdAsync(sheetId));
            Assert.Equal(onHandAfterFirst, await ReadOnHandAsync());
        }

        /// <summary>
        /// 已有盤點結果（ItemStatus != Pending）的清冊不可刪除——即使還沒結帳。
        /// 此時沒有任何過帳足跡，要守的是盤點作業本身：刪掉等於現場數過的量白數。
        /// </summary>
        [Fact]
        public async Task CountedSheet_Delete_Blocked_AndItemsUntouched()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(CountedSheet_Delete_Blocked_AndItemsUntouched));
            var (client, token) = await NewAuthedClientAsync();

            var itemsBefore = await ReadItemStatusesAsync(sheetId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{sheetId}"),
                "刪除已有盤點結果的清冊");

            Assert.Equal(itemsBefore, await ReadItemStatusesAsync(sheetId));
            Assert.True(await SheetExistsAsync(sheetId));
        }

        /// <summary>全部明細都還是 Pending 的清冊可以刪，且不留任何足跡。</summary>
        [Fact]
        public async Task PendingSheet_Delete_Succeeds_WithNoResidue()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(PendingSheet_Delete_Succeeds_WithNoResidue),
                counted: false);
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{sheetId}"),
                "刪除全 Pending 的清冊");

            Assert.False(await SheetExistsAsync(sheetId), "刪除回報成功，但清冊仍在。");
            Assert.Equal(0, await CountAdjustmentsAsync(sheetId));
            Assert.Empty(await ReadItemStatusesAsync(sheetId));
        }

        /// <summary>
        /// 已有盤點結果後不得重新產生明細快照——那會把現場數過的量整批洗掉，
        /// 而且洗掉之後帳面看不出曾經數過。
        /// </summary>
        [Fact]
        public async Task CountedSheet_GenerateItems_Blocked_AndItemsUntouched()
        {
            var sheetId = await SeedCountedSheetAsync(nameof(CountedSheet_GenerateItems_Blocked_AndItemsUntouched));
            var (client, token) = await NewAuthedClientAsync();

            var itemsBefore = await ReadItemStatusesAsync(sheetId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/GenerateItems/{sheetId}"),
                "已盤點後重新產生明細");

            Assert.Equal(itemsBefore, await ReadItemStatusesAsync(sheetId));
        }

        // ════════════════════════════════════════════════════════════════════
        // 種子與查詢
        // ════════════════════════════════════════════════════════════════════

        /// <param name="counted">true = 明細已填初盤量（可結帳）；false = 全部 Pending。</param>
        private async Task<string> SeedCountedSheetAsync(string caseName, bool counted = true)
        {
            var sheetId = $"{Prefix}IC-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";
            using var _ = NewScope(out var db);

            // 上一輪結帳產生的調整單也要清，否則「調整單張數」的斷言會累加。
            var oldAdjIds = await db.StockAdjustments.IgnoreQueryFilters()
                .Where(a => a.Remarks == AdjustmentRemark(sheetId))
                .Select(a => a.StockAdjustmentId).ToListAsync();
            if (oldAdjIds.Count > 0)
            {
                await db.StockMovements.IgnoreQueryFilters()
                    .Where(m => oldAdjIds.Contains(m.DocNo)).ExecuteDeleteAsync();
                await db.StockAdjustmentLines.IgnoreQueryFilters()
                    .Where(l => oldAdjIds.Contains(l.StockAdjustmentId)).ExecuteDeleteAsync();
                await db.StockAdjustments.IgnoreQueryFilters()
                    .Where(a => oldAdjIds.Contains(a.StockAdjustmentId)).ExecuteDeleteAsync();
            }

            await db.InventoryCountItems.IgnoreQueryFilters()
                .Where(i => i.SheetId == sheetId).ExecuteDeleteAsync();
            await db.InventoryCountSheets.IgnoreQueryFilters()
                .Where(s => s.SheetId == sheetId).ExecuteDeleteAsync();

            await EnsureMastersAsync(db);

            db.InventoryCountSheets.Add(new InventoryCountSheet
            {
                SheetId         = sheetId,
                CountMode       = "WithSheet",
                SheetStatus     = "Counting",
                SnapshotAt      = DateTime.UtcNow,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Items =
                {
                    new InventoryCountItem
                    {
                        SheetId = sheetId, SeqNo = 1,
                        WhCode = Wh, LocationCode = Loc, LotNo = "", ProductCode = Product,
                        BookQty = BookQty,
                        FirstCountQty  = counted ? CountQty : null,
                        FirstCountedBy = counted ? "admin" : null,
                        FirstCountedAt = counted ? DateTime.UtcNow : null,
                        ItemStatus     = counted ? "Counted" : "Pending",
                    },
                },
            });

            await db.SaveChangesAsync();
            return sheetId;
        }

        private static string AdjustmentRemark(string sheetId) => $"盤點結帳 {sheetId}";

        /// <summary>
        /// 找出結帳產生的調整單，並回傳它的過帳足跡。
        /// 清冊與調整單之間沒有外鍵也沒有欄位，只有 Remarks 這條字串——
        /// 這個查詢本身就是在示範這組關聯有多脆弱。
        /// </summary>
        private async Task<(string? AdjId, PostingFootprint Footprint)> CaptureCloseFootprintAsync(string sheetId)
        {
            string? adjId;
            using (var _ = NewScope(out var db))
            {
                adjId = await db.StockAdjustments.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.Remarks == AdjustmentRemark(sheetId))
                    .Select(a => a.StockAdjustmentId).FirstOrDefaultAsync();
            }
            if (adjId == null) return (null, await CaptureAsync(sheetId));
            return (adjId, await CaptureAsync(adjId));
        }

        private async Task<int> CountAdjustmentsAsync(string sheetId)
        {
            using var _ = NewScope(out var db);
            return await db.StockAdjustments.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(a => a.Remarks == AdjustmentRemark(sheetId));
        }

        private async Task<string?> ReadAdjustmentDocIdAsync(string sheetId)
        {
            using var _ = NewScope(out var db);
            return await db.InventoryCountSheets.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SheetId == sheetId)
                .Select(s => s.AdjustmentDocId).FirstAsync();
        }

        /// <summary>
        /// 直接改庫把狀態推回 Counting——刻意繞過 controller，模擬狀態守門失效。
        /// 用 ExecuteUpdate 而非載入實體再存：後者會連帶更新 xmin 與 UpdatedAt，
        /// 讓「只有狀態被動過」這個前提不成立。
        /// </summary>
        private async Task ResetSheetStatusToCountingAsync(string sheetId)
        {
            using var _ = NewScope(out var db);
            await db.InventoryCountSheets.IgnoreQueryFilters()
                .Where(s => s.SheetId == sheetId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.SheetStatus, "Counting"));
        }

        private async Task<bool> SheetExistsAsync(string sheetId)
        {
            using var _ = NewScope(out var db);
            return await db.InventoryCountSheets.IgnoreQueryFilters().AnyAsync(s => s.SheetId == sheetId);
        }

        private async Task<List<string>> ReadItemStatusesAsync(string sheetId)
        {
            using var _ = NewScope(out var db);
            return await db.InventoryCountItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SheetId == sheetId).OrderBy(i => i.SeqNo)
                .Select(i => $"{i.ItemStatus}:{i.BookQty}:{i.FirstCountQty}")
                .ToListAsync();
        }

        private async Task<decimal> ReadOnHandAsync()
        {
            using var _ = NewScope(out var db);
            return await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == Product
                            && i.LocationCode == Loc && i.LotNo == "")
                .Select(i => i.QtyOnHand).FirstAsync();
        }

        /// <summary>
        /// 本檔用自己的一組主檔（不共用 <see cref="InvDocResidueTestBase"/> 那組）：
        /// 盤點差異會實際扣減在手量，與其他測試共用同一批庫存會互相干擾。
        /// </summary>
        private static async Task EnsureMastersAsync(Data.ApplicationDbContext db)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == Uom))
                db.Uoms.Add(new Uom { UomCode = Uom, UomName = "個", DecimalPlaces = 4 });

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Category, CategoryName = "盤點殘留測試類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                });

            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Product))
                db.Products.Add(new Product
                {
                    ProductCode = Product, ProductName = "盤點殘留測試料",
                    CategoryCode = Category,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });

            // TypeCode="IC" 由系統種子提供，測試不再自己補。
            // 這裡原本有一段 if (!Any) 就自己塞一筆——那正是這個缺陷躲了這麼久的原因：
            // 正式種子從來沒有 IC，乾淨部署的盤點結帳只要有差異就丟「找不到異動類別 IC」，
            // 而共用測試庫被這段補了一筆之後，所有測試都看不到問題。
            Assert.True(
                await db.InventoryTransactionTypes.IgnoreQueryFilters().AnyAsync(t => t.TypeCode == TypeCode),
                $"異動類別 {TypeCode} 必須來自系統種子，測試不得自行補建。");

            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.WhCode == Wh))
                db.Warehouses.Add(new Warehouse { WhCode = Wh, WhName = "盤點殘留測試倉", PlantId = Plant });

            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == Wh && l.LocationCode == Loc))
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = Wh, LocationCode = Loc, LocationName = "盤點殘留測試儲位",
                });

            await db.SaveChangesAsync();

            var inv = await db.Inventories.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.PlantId == Plant && i.ProductCode == Product
                                          && i.LocationCode == Loc && i.LotNo == "");
            if (inv == null)
                db.Inventories.Add(new Inventory
                {
                    PlantId = Plant, ProductCode = Product, LocationCode = Loc, LotNo = "",
                    QtyOnHand = BookQty, QtyReserved = 0m, UnitCost = 5m,
                });
            else
            {
                inv.QtyOnHand   = BookQty;
                inv.QtyReserved = 0m;
                inv.UnitCost    = 5m;
            }

            await db.SaveChangesAsync();
        }
    }
}
