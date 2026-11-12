using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// INV「簡化模式過帳型」單據的殘留守門樣板。
    ///
    /// 報廢／借出／領樣／改修領料／委外領料這幾張單的結構是一模一樣的：
    /// Save 建單 → Confirm 直接過帳（不經倉庫作業）→ 刪除守門判 QtyPutAway &gt; 0，
    /// 而那個 QtyPutAway 是**過帳動作自己回寫的**（BuildSimplifiedRequest 的 SetQtyPutAway），
    /// 不是倉庫作業寫的。這條間接關係一旦斷掉（有人把 SetQtyPutAway 拿掉、或改走不回寫
    /// 的過帳路徑），守門會靜默失效、已過帳的單變成可刪，而既有測試沒有一支會紅。
    ///
    /// 子類只要提供「怎麼建一張已核准草稿」與「怎麼讀第一行數量」，就得到 4 條守門斷言。
    /// 各單據的 ViewModel 欄位差異太大，「修改被擋」那條留給子類自行覆寫
    /// <see cref="EditFormOrNull"/> 決定要不要測。
    /// </summary>
    public abstract class InvDocResidueTestBase : PostingResidueTestBase
    {
        protected InvDocResidueTestBase(AuthedScheduleFactory factory) : base(factory)
        {
            if (RequiresSimplifiedMode) SetSimplifiedMode("true");
        }

        /// <summary>
        /// inv_simplified_mode 是**全域**參數，測試結束一律清回未設定，
        /// 否則會污染同一測試庫的其他測試（同 collection 序列執行，清乾淨就不會互撞）。
        /// </summary>
        public override void Dispose()
        {
            if (RequiresSimplifiedMode) SetSimplifiedMode(null);
            base.Dispose();   // 前綴殘留清理
        }

        private void SetSimplifiedMode(string? value)
        {
            using var scope = Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISystemParamService>()
                .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, value).GetAwaiter().GetResult();
        }

        // ── 子類提供 ────────────────────────────────────────────────────────

        /// <summary>單據的路由前綴，例如 "/LoanDoc"。</summary>
        protected abstract string Route { get; }

        /// <summary>
        /// 本單據的 Confirm 是否需要開啟庫存簡化模式才會過帳。
        ///
        /// 這不是測試細節而是真實的行為分歧：借出／領樣／改修領料／委外領料的 Confirm
        /// 帶 simplified 分支——**完整模式下 Confirm 只把單轉 Confirmed，庫存要等
        /// 倉庫作業（WarehouseOps）才動**，此時 QtyPutAway 仍是 0，刪除守門也就不會擋。
        /// 那個行為是對的（庫存還沒動，刪掉不留孤兒），但要驗「已過帳不可刪」就必須
        /// 讓單據真的過帳，故這幾張單以簡化模式測。
        /// 報廢單沒有這個分支（一律簡化過帳），維持 false。
        ///
        /// 完整模式下「確認 → 倉庫作業出庫 → 過帳」那條路徑的守門，需要 WarehouseOps
        /// 前置，留待後續補（見 test-hardening-plan.md Phase 1 續）。
        /// </summary>
        protected virtual bool RequiresSimplifiedMode => true;

        /// <summary>建立一張「已核准、未過帳」的單並回傳單號；重跑時需自行清掉上一輪產物。</summary>
        protected abstract Task<string> SeedApprovedDraftAsync(string caseName);

        /// <summary>讀第一行的庫存數量，用來確認守門擋下時明細沒被偷偷改掉。</summary>
        protected abstract Task<decimal> ReadFirstLineStockQtyAsync(string docNo);

        /// <summary>
        /// 「修改已過帳單據」要送的表單欄位；回 null 表示本單據跳過該條測試。
        /// 各單據 VM 結構差異大，不強迫子類實作。
        /// </summary>
        protected virtual IDictionary<string, string>? EditFormOrNull(string docNo) => null;

        // ── 共用斷言 ────────────────────────────────────────────────────────

        [Fact]
        public async Task PostedDoc_Delete_Blocked_AndPostingUntouched()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(PostedDoc_Delete_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");

            // 比「有沒有留下足跡」而非「有沒有庫存異動」：備料單確認鎖的是預留不是異動，
            // 但兩者同樣是刪單後可能殘留的東西，用同一條前置條件涵蓋。
            var posted = await CaptureAsync(docNo);
            Assert.False(posted.IsEmpty,
                $"前置條件不成立：確認後應留下過帳足跡，實際 {posted}");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已過帳單據");
            await AssertUnchangedAsync(posted, docNo, "刪除被擋後");
            await AssertDocExistsAsync(docNo, expected: true, "刪除被擋下了，但單據本體卻不見了");
        }

        [Fact]
        public async Task DraftDoc_Delete_Succeeds_WithNoResidue()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(DraftDoc_Delete_Succeeds_WithNoResidue));
            var (client, token) = await NewAuthedClientAsync();

            Assert.True((await CaptureAsync(docNo)).IsEmpty, "前置條件不成立：草稿不該有過帳足跡");

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除草稿");
            await AssertNoResidueAsync(docNo, "草稿刪除後");
            await AssertDocExistsAsync(docNo, expected: false, "刪除回報成功，但單據仍在");
        }

        [Fact]
        public async Task PostedDoc_ConfirmTwice_DoesNotDoublePost()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(PostedDoc_ConfirmTwice_DoesNotDoublePost));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "第一次確認");
            var posted = await CaptureAsync(docNo);

            // 第二次應被狀態守門擋下（Status 已非 Draft）；即使守門有缺口，
            // PostDocSimplifiedAsync 的冪等檢查也會擋。兩道都失效才會重複過帳。
            await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}");
            await AssertUnchangedAsync(posted, docNo, "重複確認後");
        }

        [Fact]
        public async Task PostedDoc_Edit_Blocked_AndLinesUntouched()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(PostedDoc_Edit_Blocked_AndLinesUntouched));
            var form = EditFormOrNull(docNo);
            if (form == null) return;   // 本單據不測修改路徑

            var (client, token) = await NewAuthedClientAsync();
            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");

            var posted = await CaptureAsync(docNo);
            var qtyBefore = await ReadFirstLineStockQtyAsync(docNo);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Save", form), "修改已過帳單據");
            await AssertUnchangedAsync(posted, docNo, "修改被擋後");

            Assert.Equal(qtyBefore, await ReadFirstLineStockQtyAsync(docNo));
        }

        // ── 共用種子 ────────────────────────────────────────────────────────

        protected const string SharedProduct  = Prefix + "-P01";
        protected const string SharedCategory = Prefix + "-CAT";
        protected const string SharedUom      = Prefix + "-EA";
        protected const string SharedWh       = Prefix + "-WH";
        protected const string SharedLoc      = Prefix + "-LOC";

        /// <summary>期間鎖會擋已關帳期間的過帳；用遠期日期避開（同既有 Flows 測試手法）。</summary>
        protected static readonly DateOnly PostDate = new(2097, 6, 15);

        protected const decimal OnHand   = 1000m;
        protected const decimal LineQty  = 10m;
        protected const decimal UnitCost = 5m;

        /// <summary>
        /// 本組測試共用的主檔與期初庫存。庫存每輪重設回基準量——同 collection 多支測試
        /// 共用同一個品號／儲位，不重設的話跑到後面會可用量不足而假紅。
        /// </summary>
        protected static async Task EnsureSharedMastersAsync(ApplicationDbContext db, string typeCode, int direction)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == SharedUom))
                db.Uoms.Add(new Uom { UomCode = SharedUom, UomName = "個", DecimalPlaces = 4 });

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == SharedCategory))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = SharedCategory, CategoryName = "殘留測試類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                });

            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == SharedProduct))
                db.Products.Add(new Product
                {
                    ProductCode = SharedProduct, ProductName = "殘留測試料",
                    CategoryCode = SharedCategory,
                    PurchaseUom = SharedUom, StockUom = SharedUom, SalesUom = SharedUom,
                    IsActive = true,
                });

            if (!await db.InventoryTransactionTypes.IgnoreQueryFilters().AnyAsync(t => t.TypeCode == typeCode))
                db.InventoryTransactionTypes.Add(new InventoryTransactionType
                {
                    TypeCode = typeCode, TypeName = $"殘留測試 {typeCode}", TypeCategory = "Adjust",
                    Direction = direction, IsActive = true,
                });

            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.WhCode == SharedWh))
                db.Warehouses.Add(new Warehouse { WhCode = SharedWh, WhName = "殘留測試倉", PlantId = Plant });

            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == SharedWh && l.LocationCode == SharedLoc))
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = SharedWh, LocationCode = SharedLoc, LocationName = "殘留測試儲位",
                });

            await db.SaveChangesAsync();

            var inv = await db.Inventories.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.PlantId == Plant && i.ProductCode == SharedProduct
                                          && i.LocationCode == SharedLoc && i.LotNo == "");
            if (inv == null)
                db.Inventories.Add(new Inventory
                {
                    PlantId = Plant, ProductCode = SharedProduct, LocationCode = SharedLoc, LotNo = "",
                    QtyOnHand = OnHand, QtyReserved = 0m, UnitCost = UnitCost,
                });
            else
            {
                inv.QtyOnHand   = OnHand;
                inv.QtyReserved = 0m;
                inv.UnitCost    = UnitCost;
            }

            await db.SaveChangesAsync();
        }

        /// <summary>依測試方法名產生穩定且互不干擾的單號。</summary>
        protected string DocNoFor(string caseName, string tag)
            => $"{Prefix}{tag}-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";

        private async Task AssertDocExistsAsync(string docNo, bool expected, string because)
        {
            using var _ = NewScope(out var db);
            var exists = await DocExistsAsync(db, docNo);
            Assert.True(exists == expected, $"{because}（單號 {docNo}）。");
        }

        /// <summary>子類指出「這張單還在不在」——各單據的主鍵欄位名不同，無法共用查詢。</summary>
        protected abstract Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo);
    }
}
