using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 委外收貨單（OutsourceReceipt）的刪除／作廢 × 過帳殘留守門——**本單據原本零覆蓋**。
    ///
    /// 這是批次 D 三張單裡唯一同時碰到庫存、總帳、應付的一張，也是唯一有作廢路徑的。
    /// 四個影響斷言怎麼寫的地方：
    ///   ① 確認當下就自動產生應付發票。作廢若只改收貨單狀態，那張發票會變成掛在
    ///      作廢單上的孤兒——供應商對帳多出一筆、AP 未清餘額永遠掛帳。
    ///      程式碼註解自己講了這件事，但沒有任何測試釘住它，本組補上。
    ///   ② 簡化模式下 Confirm 直接過帳（Posted）；完整模式只轉 Confirmed，庫存要等倉庫作業。
    ///      作廢守門判的是 <c>Status == Posted</c>——**兩種模式要分開測**，
    ///      只測一種會漏掉另一種的整條路徑。
    ///   ③ 已付款的發票不能作廢，此時整個作廢動作必須擋下，不能只作廢收貨單。
    ///   ④ 在途→在手的預留轉換（§24.9(3)）掛在**委外採購單行**上而不是收貨單上，
    ///      以收貨單號查預留永遠是零筆——<see cref="PostingFootprint"/> 看不到它。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class OutsourceReceiptPostingResidueTests : MfgDocResidueTestBase
    {
        public OutsourceReceiptPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        private const string Route = "/OutsourceReceipt";

        /// <summary>
        /// inv_simplified_mode 是**全域**參數，測試結束一律清回未設定，
        /// 否則會污染同一測試庫的其他測試（同 InvDocResidueTestBase 的教訓）。
        /// </summary>
        public override void Dispose()
        {
            SetSimplifiedMode(null);
            base.Dispose();   // 前綴殘留清理
        }

        private void SetSimplifiedMode(string? value)
        {
            using var scope = Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISystemParamService>()
                .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, value).GetAwaiter().GetResult();
        }

        // ── 種子 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 建立「工單鏈 → 委外採購單（已確認）→ 已核准草稿收貨單」，回傳 (收貨單號, OPO 單號)。
        ///
        /// 委外採購單種成 Confirmed 而非 Draft：收貨畫面只列已確認的委外單，
        /// 而且「Confirmed 不可刪」正是唯一擋住收貨單變成孤兒的守門
        /// （<c>OutsourceReceipt.OutsourcePurchaseOrderId</c> 沒有外鍵）。
        /// 種在 Draft 等於測一個實際流程走不到的狀態。
        /// </summary>
        private async Task<(string ReceiptId, string OpoId)> SeedApprovedDraftAsync(string caseName)
        {
            var receiptId = MfgDocNoFor(caseName, "OR");
            var opoId     = receiptId + "-OP";
            var woId      = receiptId + "-WO";
            using var _ = NewScope(out var db);

            await ClearReceiptPostingsAsync(db, receiptId);
            await ClearOutsourcePoAsync(db, opoId);
            await EnsureMfgMastersAsync(db);
            await SeedWorkOrderChainAsync(db, woId, MfgLineQty);
            await SeedDraftOutsourcePoAsync(db, opoId, woId, MfgLineQty, transferredQty: MfgLineQty);

            var po = await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(o => o.OutsourcePurchaseOrderId == opoId);
            po.Status = Modules.MFG.Models.OutsourcePoStatus.Confirmed;

            db.OutsourceReceipts.Add(new OutsourceReceipt
            {
                OutsourceReceiptId       = receiptId,
                OutsourcePurchaseOrderId = opoId,
                ReceiptDate              = MfgPostDate,
                SupplierCode             = MfgSupplier,
                CreatedBy                = "admin",
                // TypeCode "OI"（委外進貨，Direction=+1）由 HasData 內建，不需自建測試類別。
                TypeCode                 = "OI",
                TypeCategory             = "Purchase",
                Status                   = DocStatus.Draft,
                ApprovalStatus           = DocumentApprovalStatus.Approved,
                CurrencyCode             = null,
                ExchangeRate             = 1m,
                TotalAmount              = MfgLineQty * MfgUnitCost,
                PlantId                  = Plant,
                CreatedByRoleId          = RoleId,
                Lines =
                {
                    new OutsourceReceiptLine
                    {
                        OutsourceReceiptId = receiptId, LineNo = 1,
                        OpoLineNo    = 1,
                        ProductCode  = MfgProduct,
                        ReceiptQty   = MfgLineQty, StockQty = MfgLineQty,
                        UomCode      = MfgUom, StockUomCode = MfgUom,
                        UnitCost     = MfgUnitCost, ExchangeRate = 1m,
                        LineAmount   = MfgLineQty * MfgUnitCost,
                        TotalAmount  = MfgLineQty * MfgUnitCost,
                        AmountLocal  = MfgLineQty * MfgUnitCost,
                        WhCode       = MfgWh, LocationCode = MfgLoc, LotNo = "",
                        QtyPutAway   = 0m,
                        // 0 = 不罰扣：OutsourceScrapPenaltyService.ApplyAsync 在容許報廢率為 0
                        // 時直接 no-op，種子因此不必準備罰扣的前置。
                        // null ＝ 合約未約定損耗條款 → 不扣款（本測試不驗扣款，別讓它插進來）。
                        // 0 現在的意思是「明確約定零容忍」，會真的算出罰款。
                        AllowedScrapRate = null,
                    },
                },
            });

            await db.SaveChangesAsync();
            return (receiptId, opoId);
        }

        private static async Task ClearReceiptPostingsAsync(ApplicationDbContext db, string receiptId)
        {
            await db.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == receiptId || m.DocNo.StartsWith(receiptId + "-")).ExecuteDeleteAsync();

            var apIds = await db.APInvoices.IgnoreQueryFilters()
                .Where(i => i.SourceDocId == receiptId).Select(i => i.APInvoiceId).ToListAsync();
            if (apIds.Count > 0)
            {
                await db.APInvoiceLines.IgnoreQueryFilters()
                    .Where(l => apIds.Contains(l.APInvoiceId)).ExecuteDeleteAsync();
                await db.APInvoices.IgnoreQueryFilters()
                    .Where(i => apIds.Contains(i.APInvoiceId)).ExecuteDeleteAsync();
            }

            var journalIds = await db.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == receiptId).Select(e => e.JournalId).ToListAsync();
            if (journalIds.Count > 0)
            {
                await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalId)).ExecuteDeleteAsync();
                await db.JournalEntries.IgnoreQueryFilters()
                    .Where(e => journalIds.Contains(e.JournalId)).ExecuteDeleteAsync();
            }
        }

        private async Task<DocStatus> ReadStatusAsync(string receiptId)
        {
            using var _ = NewScope(out var db);
            return await db.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OutsourceReceiptId == receiptId).Select(r => r.Status).FirstAsync();
        }

        private async Task<bool> ReceiptExistsAsync(string receiptId)
        {
            using var _ = NewScope(out var db);
            return await db.OutsourceReceipts.IgnoreQueryFilters()
                .AnyAsync(r => r.OutsourceReceiptId == receiptId);
        }

        /// <summary>回傳該收貨單的應付發票狀態；沒有發票時回 null。</summary>
        private async Task<APInvoiceStatus?> ReadApStatusAsync(string receiptId)
        {
            using var _ = NewScope(out var db);
            var statuses = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == receiptId)
                .Select(i => i.Status).ToListAsync();
            return statuses.Count == 0 ? null : statuses[0];
        }

        // ── 簡化模式：確認即過帳 ────────────────────────────────────────────

        /// <summary>
        /// 簡化模式確認一次，必須同時留下庫存異動與應付發票兩方足跡。
        /// 這是後面所有「不得被動到」斷言的地基——沒有足跡時，
        /// 「足跡不變」是恆真句，守門測試會全部假綠。
        /// </summary>
        [Fact]
        public async Task Simplified_Confirm_LeavesInventoryAndApFootprints()
        {
            SetSimplifiedMode("true");
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(Simplified_Confirm_LeavesInventoryAndApFootprints));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "確認過帳");

            var f = await CaptureAsync(receiptId);
            Assert.True(f.MovementLines > 0, $"委外收貨應產生庫存異動，實際 {f}");
            Assert.Equal(1, f.ApInvoices);
            Assert.Equal(DocStatus.Posted, await ReadStatusAsync(receiptId));
        }

        /// <summary>
        /// 已過帳的委外收貨單不可刪，且庫存、傳票、應付發票都不得被動到。
        ///
        /// 這張單刪掉的殺傷力比一般進貨更大：庫存異動、應付發票、以及掛在委外採購單行上的
        /// 在途→在手轉換，三者各自指向不同的鍵，沒有任何一個有外鍵指回收貨單。
        /// 守門破了就是三處同時變孤兒，而且分屬三個模組，對帳時看起來像三個不相干的問題。
        /// </summary>
        [Fact]
        public async Task PostedReceipt_Delete_Blocked_AndPostingUntouched()
        {
            SetSimplifiedMode("true");
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(PostedReceipt_Delete_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "確認過帳");
            var posted = await CaptureAsync(receiptId);
            Assert.False(posted.IsEmpty, $"前置條件不成立：確認後應留下足跡，實際 {posted}");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{receiptId}"),
                "刪除已過帳委外收貨單");

            await AssertUnchangedAsync(posted, receiptId, "刪除被擋後");
            Assert.True(await ReceiptExistsAsync(receiptId), "刪除被擋下了，但單據本體卻不見了");
        }

        /// <summary>
        /// 已過帳的委外收貨單不可作廢——庫存已經進來了，作廢只改狀態不會把貨退回去。
        /// 這條與刪除守門不是重複：作廢是**合法的日常動作**（完整模式下確認後就能作廢），
        /// 只有 Posted 這一格被擋住。守門若移位，已入庫的貨會在帳上憑空消失。
        /// </summary>
        [Fact]
        public async Task PostedReceipt_Void_Blocked_AndPostingUntouched()
        {
            SetSimplifiedMode("true");
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(PostedReceipt_Void_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "確認過帳");
            var posted = await CaptureAsync(receiptId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Void/{receiptId}"),
                "作廢已過帳委外收貨單");

            Assert.Equal(DocStatus.Posted, await ReadStatusAsync(receiptId));
            await AssertUnchangedAsync(posted, receiptId, "作廢被擋後");
            Assert.Equal(APInvoiceStatus.Open, await ReadApStatusAsync(receiptId));
        }

        /// <summary>
        /// 重複確認不得重複過帳。第二次應被 <c>Status != Draft</c> 守門擋下；
        /// 即使守門有缺口，PostDocSimplifiedAsync 的冪等檢查也會擋。
        /// 兩道都失效才會庫存扣兩次、應付開兩張。
        /// </summary>
        [Fact]
        public async Task PostedReceipt_ConfirmTwice_DoesNotDoublePost()
        {
            SetSimplifiedMode("true");
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(PostedReceipt_ConfirmTwice_DoesNotDoublePost));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "第一次確認");
            var posted = await CaptureAsync(receiptId);

            await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}");

            await AssertUnchangedAsync(posted, receiptId, "重複確認後");
        }

        // ── 完整模式：確認 → 作廢必須連同應付一起沖掉 ───────────────────────

        /// <summary>
        /// 完整模式下確認只轉 Confirmed（庫存要等倉庫作業），但**應付發票當下就開出來了**。
        ///
        /// 這是這張單最容易漏的一段：確認後作廢是合法動作，若作廢只改收貨單狀態，
        /// 那張已開立的應付發票會變成掛在作廢單上的孤兒——供應商對帳多出一筆、
        /// AP 未清餘額永遠掛帳，而收貨單本身看起來乾乾淨淨。
        /// 程式碼註解點名了這個風險，本測試是唯一釘住它的東西。
        /// </summary>
        [Fact]
        public async Task Full_ConfirmThenVoid_AlsoVoidsApInvoice()
        {
            SetSimplifiedMode(null);   // 完整模式
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(Full_ConfirmThenVoid_AlsoVoidsApInvoice));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "確認（完整模式）");

            Assert.Equal(DocStatus.Confirmed, await ReadStatusAsync(receiptId));
            Assert.Equal(APInvoiceStatus.Open, await ReadApStatusAsync(receiptId));
            Assert.Equal(0, (await CaptureAsync(receiptId)).MovementLines);   // 庫存還沒動

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Void/{receiptId}"), "作廢已確認委外收貨單");

            Assert.Equal(DocStatus.Void, await ReadStatusAsync(receiptId));
            Assert.Equal(APInvoiceStatus.Void, await ReadApStatusAsync(receiptId));
        }

        /// <summary>
        /// 作廢後的委外收貨單仍不可刪——作廢是留痕，刪除是抹除。
        /// 若這格被放寬，「作廢 → 刪除」兩步就能讓已沖銷的應付發票失去來源單據，
        /// 而每一步各自看起來都合法。
        /// </summary>
        [Fact]
        public async Task VoidedReceipt_Delete_Blocked()
        {
            SetSimplifiedMode(null);
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(VoidedReceipt_Delete_Blocked));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{receiptId}"), "確認（完整模式）");
            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Void/{receiptId}"), "作廢");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{receiptId}"),
                "刪除已作廢委外收貨單");

            Assert.True(await ReceiptExistsAsync(receiptId), "刪除被擋下了，但單據本體卻不見了");
        }

        // ── 草稿 ────────────────────────────────────────────────────────────

        /// <summary>草稿委外收貨單可刪，且刪完不留任何過帳殘留。</summary>
        [Fact]
        public async Task DraftReceipt_Delete_Succeeds_WithNoResidue()
        {
            var (receiptId, _) = await SeedApprovedDraftAsync(nameof(DraftReceipt_Delete_Succeeds_WithNoResidue));
            var (client, token) = await NewAuthedClientAsync();

            Assert.True((await CaptureAsync(receiptId)).IsEmpty, "前置條件不成立：草稿不該有過帳足跡");

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{receiptId}"),
                "刪除草稿委外收貨單");

            await AssertNoResidueAsync(receiptId, "草稿刪除後");
            Assert.False(await ReceiptExistsAsync(receiptId), "刪除回報成功，但單據仍在");
            Assert.Null(await ReadApStatusAsync(receiptId));
        }
    }
}
