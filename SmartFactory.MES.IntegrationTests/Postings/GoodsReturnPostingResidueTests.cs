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
    /// 採購退貨單（GoodsReturn）的刪除／修改 × 過帳殘留守門。
    ///
    /// 這張單的過帳足跡有一半**不在自己身上**：庫存異動與傳票掛在退貨單號下，
    /// 但應付的變動是去**負向沖銷來源進貨單的那張 AP 發票**
    /// （<c>ReverseForReturnedQtyAsync</c> 直接改 APInvoiceLine.Qty 與金額）。
    /// 所以只查退貨單號的足跡會看起來乾乾淨淨，而供應商實際上已經少請款一筆。
    ///
    /// 這正是刪除守門一旦破掉最難查的形態：退貨單沒了，來源進貨單的應付金額卻已被改小，
    /// 對帳時只會看到「這張進貨的應付金額和進貨金額對不起來」，追不到是誰改的。
    /// 故 <see cref="PostedReturn_Delete_Blocked_AndSourceApReversalUntouched"/>
    /// 把來源 AP 的金額也一併釘住。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class GoodsReturnPostingResidueTests : InvDocResidueTestBase
    {
        private const string ReturnType  = Prefix + "-PF";
        private const string ReceiptType = Prefix + "-PI2";
        private const string Supplier    = Prefix + "-SUPR";

        public GoodsReturnPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/GoodsReturn";

        /// <summary>完整模式下 Confirm 只轉 Confirmed，庫存與 AP 回沖都還沒發生。</summary>
        protected override bool RequiresSimplifiedMode => true;

        protected override Task<string> SeedApprovedDraftAsync(string caseName)
            => SeedAsync(caseName, withSourceReceipt: false);

        private async Task<string> SeedAsync(string caseName, bool withSourceReceipt)
        {
            var docNo = DocNoFor(caseName, "PF");
            var grNo  = docNo + "-GR";
            using var _ = NewScope(out var db);

            await ClearPostingsAsync(db, docNo);
            await ClearPostingsAsync(db, grNo);
            await db.GoodsReturnLines.IgnoreQueryFilters()
                .Where(l => l.GoodsReturnId == docNo).ExecuteDeleteAsync();
            await db.GoodsReturns.IgnoreQueryFilters()
                .Where(d => d.GoodsReturnId == docNo).ExecuteDeleteAsync();
            await db.GoodsReceiptLines.IgnoreQueryFilters()
                .Where(l => l.GoodsReceiptId == grNo).ExecuteDeleteAsync();
            await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(d => d.GoodsReceiptId == grNo).ExecuteDeleteAsync();

            // 退貨是出庫；同時備妥進貨用的入庫類別。
            await EnsureSharedMastersAsync(db, ReturnType, direction: -1);
            await EnsureSharedMastersAsync(db, ReceiptType, direction: 1);
            await EnsureSupplierAsync(db);

            if (withSourceReceipt)
            {
                db.GoodsReceipts.Add(new GoodsReceipt
                {
                    GoodsReceiptId  = grNo,
                    ReceiptDate     = PostDate,
                    SupplierCode    = Supplier,
                    TypeCode        = ReceiptType,
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
                            GoodsReceiptId = grNo, LineNo = 1, ProductCode = SharedProduct,
                            ReceiptQty = LineQty, StockQty = LineQty,
                            UomCode = SharedUom, StockUomCode = SharedUom,
                            UnitPrice = UnitCost, ExchangeRate = 1m,
                            LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                            AmountLocal = LineQty * UnitCost,
                            WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                        },
                    },
                });
            }

            db.GoodsReturns.Add(new GoodsReturn
            {
                GoodsReturnId   = docNo,
                ReturnDate      = PostDate,
                SupplierCode    = Supplier,
                TypeCode        = ReturnType,
                TypeCategory    = "Purchase",
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                TotalAmount     = LineQty * UnitCost,
                Lines = new List<GoodsReturnLine>
                {
                    new()
                    {
                        GoodsReturnId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        GoodsReceiptId = withSourceReceipt ? grNo : null,
                        ReceiptLineNo  = withSourceReceipt ? 1 : null,
                        ReturnQty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        UnitPrice = UnitCost, ExchangeRate = 1m,
                        LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                        AmountLocal = LineQty * UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                        QtyPutAway = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.GoodsReturnLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsReturnId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.GoodsReturns.IgnoreQueryFilters().AnyAsync(d => d.GoodsReturnId == docNo);

        // ── 本單據專屬：跨單據的應付回沖 ────────────────────────────────────

        /// <summary>
        /// 已過帳的退貨單不可刪，且**來源進貨單的應付金額**也不得因此被動到。
        ///
        /// 這條的價值在於它同時驗兩個方向：退貨單自己的庫存／傳票足跡，
        /// 以及被它改小的那張 AP 發票。後者不在退貨單號底下，
        /// 任何只查本單足跡的斷言都看不見。
        /// </summary>
        [Fact]
        public async Task PostedReturn_Delete_Blocked_AndSourceApReversalUntouched()
        {
            var docNo = await SeedAsync(nameof(PostedReturn_Delete_Blocked_AndSourceApReversalUntouched),
                withSourceReceipt: true);
            var grNo  = docNo + "-GR";
            var (client, token) = await NewAuthedClientAsync();

            // 來源進貨先過帳，才會有 AP 發票可供退貨回沖。
            AssertAllowed(await PostActionAsync(client, token, $"/GoodsReceipt/Confirm/{grNo}"), "來源進貨確認過帳");
            var apAfterReceipt = await ReadApTotalAsync(grNo);
            Assert.True(apAfterReceipt > 0m, $"前置條件不成立：進貨應認列應付，實際 {apAfterReceipt}");

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "退貨確認過帳");

            var apAfterReturn = await ReadApTotalAsync(grNo);
            Assert.True(apAfterReturn < apAfterReceipt,
                $"前置條件不成立：退貨應負向回沖來源應付（{apAfterReceipt} → {apAfterReturn}）。");

            var posted = await CaptureAsync(docNo);
            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已過帳退貨單");

            await AssertUnchangedAsync(posted, docNo, "刪除被擋後");
            Assert.Equal(apAfterReturn, await ReadApTotalAsync(grNo));
        }

        // ── 種子輔助 ────────────────────────────────────────────────────────

        /// <summary>來源進貨單那張 AP 發票的含稅總額（找不到回 0）。</summary>
        private async Task<decimal> ReadApTotalAsync(string goodsReceiptId)
        {
            using var _ = NewScope(out var db);
            return await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == goodsReceiptId)
                .Select(i => i.TotalAmount).FirstOrDefaultAsync();
        }

        private static async Task ClearPostingsAsync(ApplicationDbContext db, string docNo)
        {
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();

            var apIds = await db.APInvoices.IgnoreQueryFilters()
                .Where(i => i.SourceDocId == docNo).Select(i => i.APInvoiceId).ToListAsync();
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
                CompanyCode = Supplier, CompanyName = "殘留測試退貨供應商",
                IsSupplier = true, IsCustomer = false, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
    }
}
