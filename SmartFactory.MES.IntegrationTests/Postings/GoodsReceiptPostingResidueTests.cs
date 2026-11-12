using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 進貨單（GoodsReceipt）的刪除／修改 × 過帳殘留守門。
    ///
    /// 全庫足跡最完整的一張單：簡化模式下確認一次同時產生
    /// **庫存異動 + 傳票 + 應付發票**，另外還回寫上游採購單的 ReceivedQty。
    /// 也就是說它有四個方向可以留下爛帳，而其中「採購單累計收貨量」那個方向
    /// 連 <see cref="PostingResidueTestBase.PostingFootprint"/> 都涵蓋不到——
    /// 那是跨單據的欄位回寫，刪單時若沒回補，採購單會永遠顯示超收，
    /// 之後這張 PO 剩餘可收量算出來是負的，採購端卻查不到多收的貨去哪了。
    /// 故本檔在共用斷言外，額外釘住 <see cref="DraftDelete_RestoresPurchaseOrderReceivedQty"/>。
    ///
    /// 守門是雙層的：<c>PurchaseService.DeleteGoodsReceiptAsync</c> 判 <c>Status != Draft</c>，
    /// Controller 另外判 <c>QtyPutAway > 0</c> 與 <c>ReturnedQty > 0</c>。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class GoodsReceiptPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-PI";
        private const string Supplier = Prefix + "-SUPP";

        public GoodsReceiptPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/GoodsReceipt";

        /// <summary>
        /// 完整模式下 Confirm 只把單轉 Confirmed（貨待上架），庫存與 AP 都還沒動；
        /// 要驗「已過帳不可刪」就得讓它真的過帳。
        /// </summary>
        protected override bool RequiresSimplifiedMode => true;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
            => await SeedAsync(caseName, withPurchaseOrder: false);

        private async Task<string> SeedAsync(string caseName, bool withPurchaseOrder)
        {
            var docNo = DocNoFor(caseName, "PE");
            var poNo  = docNo + "-PO";
            using var _ = NewScope(out var db);

            await ClearApAsync(db, docNo);
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.GoodsReceiptLines.IgnoreQueryFilters()
                .Where(l => l.GoodsReceiptId == docNo).ExecuteDeleteAsync();
            await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(d => d.GoodsReceiptId == docNo).ExecuteDeleteAsync();
            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(o => o.PurchaseOrderId == poNo).ExecuteDeleteAsync();

            // 進貨是入庫（Direction = +1），不吃期初可用量。
            await EnsureSharedMastersAsync(db, TypeCode, direction: 1);
            await EnsureSupplierAsync(db);

            if (withPurchaseOrder)
            {
                db.PurchaseOrders.Add(new PurchaseOrder
                {
                    PurchaseOrderId = poNo,
                    OrderDate       = PostDate,
                    SupplierCode    = Supplier,
                    CreatedBy       = "admin",
                    PlantId         = Plant,
                    CreatedByRoleId = RoleId,
                    ApprovalStatus  = DocumentApprovalStatus.Approved,
                    Lines =
                    {
                        new PurchaseOrderLine
                        {
                            PurchaseOrderId = poNo, LineNo = 1, ProductCode = SharedProduct,
                            OrderQty = LineQty * 2, StockQty = LineQty * 2,
                            UomCode = SharedUom, StockUomCode = SharedUom,
                            UnitPrice = UnitCost,
                            // 進貨單建立時上游已累加過收貨量；刪單必須把這筆減回去。
                            ReceivedQty = LineQty,
                        },
                    },
                });
            }

            db.GoodsReceipts.Add(new GoodsReceipt
            {
                GoodsReceiptId  = docNo,
                ReceiptDate     = PostDate,
                SupplierCode    = Supplier,
                TypeCode        = TypeCode,
                TypeCategory    = "Purchase",
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                TotalAmount     = LineQty * UnitCost,
                Lines = new List<GoodsReceiptLine>
                {
                    new()
                    {
                        GoodsReceiptId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        PurchaseOrderId = withPurchaseOrder ? poNo : null,
                        PoLineNo        = withPurchaseOrder ? 1 : null,
                        ReceiptQty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        UnitPrice = UnitCost, ExchangeRate = 1m,
                        LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                        AmountLocal = LineQty * UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                        QtyPutAway = 0m, ReturnedQty = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsReceiptId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.GoodsReceipts.IgnoreQueryFilters().AnyAsync(d => d.GoodsReceiptId == docNo);

        // ── 本單據專屬 ──────────────────────────────────────────────────────

        /// <summary>
        /// 確認一次必須同時留下庫存、總帳、應付三方足跡。
        ///
        /// 這條是後面所有守門斷言的前提：若哪天 AP 認列時點改了（例如全部移到上架才認列），
        /// 這裡會紅，提醒回頭確認「刪除守門擋的到底是哪幾種殘留」——
        /// 曾經發生過的缺陷正是 AP 只在手動 Confirm 產生、另外兩條完成路徑漏呼叫。
        /// </summary>
        [Fact]
        public async Task Confirm_LeavesInventoryJournalAndApFootprints()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(Confirm_LeavesInventoryJournalAndApFootprints));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");

            var f = await CaptureAsync(docNo);
            Assert.True(f.MovementLines > 0, $"應產生庫存異動，實際 {f}");
            Assert.True(f.JournalEntries > 0, $"應產生傳票，實際 {f}");
            Assert.Equal(1, f.ApInvoices);
        }

        /// <summary>
        /// 草稿進貨單刪除時，必須把上游採購單的累計收貨量減回去。
        ///
        /// 這個方向不在過帳足跡裡：PO.ReceivedQty 是單純的欄位回寫，沒有異動、沒有傳票，
        /// 漏回補時完全無聲——採購單看起來收足了，剩餘可收量算成 0（或負數），
        /// 之後補收的貨沒有 PO 可掛，而帳上又找不到多收的那批。
        /// </summary>
        [Fact]
        public async Task DraftDelete_RestoresPurchaseOrderReceivedQty()
        {
            var docNo = await SeedAsync(nameof(DraftDelete_RestoresPurchaseOrderReceivedQty), withPurchaseOrder: true);
            var poNo  = docNo + "-PO";
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(LineQty, await ReadPoReceivedQtyAsync(poNo));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除草稿進貨單");

            await AssertNoResidueAsync(docNo, "草稿刪除後");
            Assert.Equal(0m, await ReadPoReceivedQtyAsync(poNo));
        }

        /// <summary>
        /// 已過帳的進貨單刪除被擋下時，上游採購單的收貨量同樣不得被動到。
        ///
        /// 守門擋下與「擋下但已經先把 PO 減回去了」在畫面上長得一模一樣，
        /// 差別只在採購單那個數字——而那是刪除守門最容易寫錯的順序問題
        /// （先回補、後檢查）。
        /// </summary>
        [Fact]
        public async Task PostedDelete_Blocked_AndPurchaseOrderReceivedQtyUntouched()
        {
            var docNo = await SeedAsync(nameof(PostedDelete_Blocked_AndPurchaseOrderReceivedQtyUntouched),
                withPurchaseOrder: true);
            var poNo  = docNo + "-PO";
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");
            var posted = await CaptureAsync(docNo);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已過帳進貨單");

            await AssertUnchangedAsync(posted, docNo, "刪除被擋後");
            Assert.Equal(LineQty, await ReadPoReceivedQtyAsync(poNo));
        }

        // ── 種子輔助 ────────────────────────────────────────────────────────

        private async Task<decimal> ReadPoReceivedQtyAsync(string poNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseOrderId == poNo && l.LineNo == 1)
                .Select(l => l.ReceivedQty).FirstAsync();
        }

        /// <summary>AP 發票與其明細、傳票都掛在進貨單號下，重跑前一併清掉。</summary>
        private static async Task ClearApAsync(ApplicationDbContext db, string docNo)
        {
            var apIds = await db.APInvoices.IgnoreQueryFilters()
                .Where(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == docNo)
                .Select(i => i.APInvoiceId).ToListAsync();
            if (apIds.Count > 0)
            {
                await db.APInvoiceLines.IgnoreQueryFilters()
                    .Where(l => apIds.Contains(l.APInvoiceId)).ExecuteDeleteAsync();
                await db.APInvoices.IgnoreQueryFilters()
                    .Where(i => apIds.Contains(i.APInvoiceId)).ExecuteDeleteAsync();
            }

            var journalIds = await db.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == docNo).Select(e => e.JournalId).ToListAsync();
            if (journalIds.Count > 0)
            {
                await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalId)).ExecuteDeleteAsync();
                await db.JournalEntries.IgnoreQueryFilters()
                    .Where(e => journalIds.Contains(e.JournalId)).ExecuteDeleteAsync();
            }
        }

        private static async Task EnsureSupplierAsync(ApplicationDbContext db)
        {
            if (await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier)) return;
            db.Companies.Add(new Company
            {
                CompanyCode = Supplier, CompanyName = "殘留測試供應商",
                IsSupplier = true, IsCustomer = false, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
    }
}
