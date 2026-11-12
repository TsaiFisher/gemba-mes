using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 發退料單（MaterialSlip）的刪除／取消 × 過帳殘留守門——**本單據原本零覆蓋**。
    ///
    /// 這張單是全庫最值得釘的一張：它直接動庫存，而且**沒有任何回沖路徑**。
    /// 沒有作廢、沒有反過帳，過帳後唯一的出口就是「不准動」——
    /// 所以守門一旦破，產生的殘留永遠不會有人來清。
    ///
    /// 三個結構上與其他單據不同、會影響斷言怎麼寫的地方：
    ///   ① 過帳動作叫 <c>Issue</c> 不叫 Confirm——<c>Confirm</c> 只是 Picking → Confirmed，
    ///      庫存完全沒動。照別張單的慣例打 Confirm 會得到一支「什麼都沒驗到」的綠燈測試。
    ///   ② 刪除守門判的是 <c>Status</c>（僅 Draft／Cancelled 可刪），不是 QtyPutAway。
    ///      取消守門另外判 Confirmed／Posted——兩道都要釘，因為 Cancelled 是可刪狀態，
    ///      「已過帳的單能被取消」等於繞過刪除守門的後門。
    ///   ③ 過帳會回寫 <c>WorkOrderMaterial.IssuedQty</c>。那是純欄位回寫，
    ///      不留異動也不留傳票，<see cref="PostingFootprint"/> 完全看不到它，
    ///      任何以單號查足跡的斷言都會漏掉——本專案第三種殘留形態。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MaterialSlipPostingResidueTests : MfgDocResidueTestBase
    {
        public MaterialSlipPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        private const string Route = "/MaterialSlip";

        // ── 種子 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 建立「工單鏈 + 一張 Confirmed 領料單」並回傳 (單號, 工單號)。
        ///
        /// 直接種在 Confirmed 而不是走 Draft→Approve→Pick→Confirm 四段 HTTP：
        /// 要驗的是**過帳之後**的守門，前面四段狀態轉換與殘留無關，
        /// 走完只會讓失敗訊息指向不相干的那一段。
        /// </summary>
        private async Task<(string SlipId, string WorkOrderId)> SeedConfirmedIssueSlipAsync(string caseName)
        {
            var slipId = MfgDocNoFor(caseName, "MS");
            var woId   = slipId + "-WO";
            using var _ = NewScope(out var db);

            await ClearSlipPostingsAsync(db, slipId);
            await EnsureMfgMastersAsync(db);
            await SeedWorkOrderChainAsync(db, woId, MfgLineQty);

            db.MaterialSlips.Add(new MaterialSlip
            {
                SlipId        = slipId,
                SlipNo        = slipId,
                Category      = MaterialSlipCategory.InternalIssue,
                Status        = MaterialSlipStatus.Confirmed,
                SlipDate      = MfgPostDate,
                SourceDocType = "WorkOrder",
                SourceDocId   = woId,
                PlantId       = Plant,
                Lines =
                {
                    new MaterialSlipLine
                    {
                        SlipId = slipId, LineNo = 1,
                        // "{OperationSeq}_{BomSeq}"：回寫 WorkOrderMaterial 靠這個字串對應，
                        // 對不上就靜默跳過（見 BackfillWorkOrderMaterialQtyAsync）。
                        SourceLineId      = "1_1",
                        ProductCode       = MfgMaterial,
                        RequestedQty      = MfgLineQty,
                        ConfirmedQty      = MfgLineQty,
                        UomCode           = MfgUom,
                        FromWarehouseCode = MfgWh,
                        FromLocationCode  = MfgLoc,
                        LotNo             = "",
                    },
                },
            });

            await db.SaveChangesAsync();
            return (slipId, woId);
        }

        /// <summary>草稿領料單（未確認、未過帳），用來驗「可刪且刪乾淨」。</summary>
        private async Task<string> SeedDraftSlipAsync(string caseName)
        {
            var (slipId, _) = await SeedConfirmedIssueSlipAsync(caseName);
            using var _s = NewScope(out var db);
            var slip = await db.MaterialSlips.IgnoreQueryFilters().FirstAsync(s => s.SlipId == slipId);
            slip.Status = MaterialSlipStatus.Draft;
            await db.SaveChangesAsync();
            return slipId;
        }

        private static async Task ClearSlipPostingsAsync(ApplicationDbContext db, string slipId)
        {
            await db.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == slipId || m.DocNo.StartsWith(slipId + "-")).ExecuteDeleteAsync();

            var journalIds = await db.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == slipId).Select(e => e.JournalId).ToListAsync();
            if (journalIds.Count > 0)
            {
                await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalId)).ExecuteDeleteAsync();
                await db.JournalEntries.IgnoreQueryFilters()
                    .Where(e => journalIds.Contains(e.JournalId)).ExecuteDeleteAsync();
            }
        }

        private async Task<MaterialSlipStatus> ReadStatusAsync(string slipId)
        {
            using var _ = NewScope(out var db);
            return await db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SlipId == slipId).Select(s => s.Status).FirstAsync();
        }

        private async Task<bool> SlipExistsAsync(string slipId)
        {
            using var _ = NewScope(out var db);
            return await db.MaterialSlips.IgnoreQueryFilters().AnyAsync(s => s.SlipId == slipId);
        }

        // ── 過帳足跡本身 ────────────────────────────────────────────────────

        /// <summary>
        /// 前置條件測試：過帳確實同時留下庫存異動**與**工單用料回寫。
        ///
        /// 這條看似多餘，但它是後面所有斷言的地基：若哪天 Issue 悄悄不再回寫
        /// <c>IssuedQty</c>（例如 SourceLineId 慣例改了），後面的守門測試仍會全綠，
        /// 因為它們比的是「不變」——0 也是不變。這支是唯一會紅的。
        /// </summary>
        [Fact]
        public async Task Issue_LeavesInventoryMovementAndBackfillsWorkOrderMaterial()
        {
            var (slipId, woId) = await SeedConfirmedIssueSlipAsync(
                nameof(Issue_LeavesInventoryMovementAndBackfillsWorkOrderMaterial));
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal((0m, 0m), await ReadWorkOrderMaterialQtyAsync(woId));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Issue/{slipId}"), "過帳發料");

            var f = await CaptureAsync(slipId);
            Assert.True(f.MovementLines > 0, $"發料應產生庫存異動，實際 {f}");

            var (issued, returned) = await ReadWorkOrderMaterialQtyAsync(woId);
            Assert.Equal(MfgLineQty, issued);
            Assert.Equal(0m, returned);
        }

        // ── 刪除守門 ────────────────────────────────────────────────────────

        /// <summary>
        /// 已過帳的領料單不可刪，且庫存異動與工單用料累計量都不得被動到。
        ///
        /// 這張單沒有回沖路徑：若守門破了，庫存已扣、IssuedQty 已加，而單據本體消失——
        /// 異動的 DocNo 指向一張查不到的單，工單的已發料量再也無從追溯是哪一次發的。
        /// StockMovement 對單據沒有外鍵，資料庫不會攔。
        /// </summary>
        [Fact]
        public async Task PostedSlip_Delete_Blocked_AndPostingUntouched()
        {
            var (slipId, woId) = await SeedConfirmedIssueSlipAsync(
                nameof(PostedSlip_Delete_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Issue/{slipId}"), "過帳發料");
            var posted = await CaptureAsync(slipId);
            Assert.False(posted.IsEmpty, $"前置條件不成立：過帳後應留下足跡，實際 {posted}");
            var qtyBefore = await ReadWorkOrderMaterialQtyAsync(woId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{slipId}"),
                "刪除已過帳領料單");

            await AssertUnchangedAsync(posted, slipId, "刪除被擋後");
            Assert.Equal(qtyBefore, await ReadWorkOrderMaterialQtyAsync(woId));
            Assert.True(await SlipExistsAsync(slipId), "刪除被擋下了，但單據本體卻不見了");
        }

        /// <summary>
        /// 已過帳的領料單不可取消。
        ///
        /// 取消守門與刪除守門不是重複：Cancelled 是**可刪狀態**。
        /// 若已過帳的單能被取消，就等於「取消 → 刪除」兩步繞過刪除守門，
        /// 而每一步各自看起來都合法。這種兩段式後門是狀態機最典型的破口。
        /// </summary>
        [Fact]
        public async Task PostedSlip_Cancel_Blocked_AndPostingUntouched()
        {
            var (slipId, woId) = await SeedConfirmedIssueSlipAsync(
                nameof(PostedSlip_Cancel_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Issue/{slipId}"), "過帳發料");
            var posted = await CaptureAsync(slipId);
            var qtyBefore = await ReadWorkOrderMaterialQtyAsync(woId);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Cancel/{slipId}"),
                "取消已過帳領料單");

            Assert.Equal(MaterialSlipStatus.Posted, await ReadStatusAsync(slipId));
            await AssertUnchangedAsync(posted, slipId, "取消被擋後");
            Assert.Equal(qtyBefore, await ReadWorkOrderMaterialQtyAsync(woId));
        }

        /// <summary>
        /// 已確認（揀料完成、尚未過帳）的領料單可以取消（2026-10-20 A6 改裁定）：原本不可取消，
        /// 工單要作廢只能先過帳再開退料單，平白多兩筆庫存異動。確認只改狀態，取消後
        /// 不得留下任何過帳足跡（揀出的料由倉庫放回原儲位）。刪除仍只放行草稿／已取消（見下一支）。
        /// </summary>
        [Fact]
        public async Task ConfirmedSlip_Cancel_AllowedWithoutPostingFootprint()
        {
            var (slipId, _) = await SeedConfirmedIssueSlipAsync(nameof(ConfirmedSlip_Cancel_AllowedWithoutPostingFootprint));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Cancel/{slipId}"),
                "取消已確認領料單");

            Assert.Equal(MaterialSlipStatus.Cancelled, await ReadStatusAsync(slipId));
            Assert.True((await CaptureAsync(slipId)).IsEmpty, "取消的領料單不該有過帳足跡");
        }

        /// <summary>
        /// 已確認但未過帳的領料單也不可刪（守門只放行 Draft／Cancelled）。
        /// 此時沒有任何過帳足跡，刪掉不會留孤兒——擋下的理由是流程完整性不是殘留。
        /// 一併釘住，是因為這格若被放寬，「確認 → 刪除」會讓倉庫備好的料憑空消失在系統裡。
        /// </summary>
        [Fact]
        public async Task ConfirmedSlip_Delete_Blocked()
        {
            var (slipId, _) = await SeedConfirmedIssueSlipAsync(nameof(ConfirmedSlip_Delete_Blocked));
            var (client, token) = await NewAuthedClientAsync();

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{slipId}"),
                "刪除已確認領料單");

            Assert.True(await SlipExistsAsync(slipId), "刪除被擋下了，但單據本體卻不見了");
        }

        /// <summary>草稿領料單可刪，且刪完不留任何過帳殘留與工單回寫。</summary>
        [Fact]
        public async Task DraftSlip_Delete_Succeeds_WithNoResidue()
        {
            var slipId = await SeedDraftSlipAsync(nameof(DraftSlip_Delete_Succeeds_WithNoResidue));
            var woId   = slipId + "-WO";
            var (client, token) = await NewAuthedClientAsync();

            Assert.True((await CaptureAsync(slipId)).IsEmpty, "前置條件不成立：草稿不該有過帳足跡");

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{slipId}"),
                "刪除草稿領料單");

            await AssertNoResidueAsync(slipId, "草稿刪除後");
            Assert.False(await SlipExistsAsync(slipId), "刪除回報成功，但單據仍在");
            Assert.Equal((0m, 0m), await ReadWorkOrderMaterialQtyAsync(woId));
        }

        // ── 重複過帳 ────────────────────────────────────────────────────────

        /// <summary>
        /// 重複過帳不得重複扣庫存、也不得把 <c>IssuedQty</c> 加第二次。
        ///
        /// 兩道防線：Status != Confirmed 的狀態守門，以及 PostDocSimplifiedAsync 自身的
        /// 冪等檢查。兩道都失效才會雙重發料——但 <c>IssuedQty</c> 的累加**不在**
        /// 任何一道防線的保護語意裡（它在過帳成功之後才執行），所以要分開斷言：
        /// 足跡不變不等於回寫沒被加第二次。
        /// </summary>
        [Fact]
        public async Task PostedSlip_IssueTwice_DoesNotDoublePostNorDoubleBackfill()
        {
            var (slipId, woId) = await SeedConfirmedIssueSlipAsync(
                nameof(PostedSlip_IssueTwice_DoesNotDoublePostNorDoubleBackfill));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Issue/{slipId}"), "第一次過帳");
            var posted = await CaptureAsync(slipId);
            var qtyAfterFirst = await ReadWorkOrderMaterialQtyAsync(woId);

            await PostActionAsync(client, token, $"{Route}/Issue/{slipId}");

            await AssertUnchangedAsync(posted, slipId, "重複過帳後");
            Assert.Equal(qtyAfterFirst, await ReadWorkOrderMaterialQtyAsync(woId));
        }

        // ── 修改路徑（Phase 5 補的那一格）────────────────────────────────────

        /// <summary>
        /// 已過帳的領料單不可修改。
        ///
        /// 本檔原本只覆蓋刪除與取消，修改路徑是空的——而修改比刪除更難察覺：
        /// 刪掉一張已過帳的單至少會讓列表少一筆，改掉數量卻是庫存異動與單據明細
        /// 從此各說各話，兩邊都還在，只有對帳時才會發現差額。
        ///
        /// 斷言比對訊息而不只是「沒成功」：MaterialSlipController.Save 直接綁
        /// <c>MaterialSlip</c> entity，ModelState 不合法時同樣回 PartialView，
        /// 與守門擋下長得一模一樣。少了訊息比對，表單欄位打錯字也會讓這支測試綠。
        /// </summary>
        [Fact]
        public async Task PostedSlip_Edit_Blocked_AndPostingUntouched()
        {
            var (slipId, woId) = await SeedConfirmedIssueSlipAsync(
                nameof(PostedSlip_Edit_Blocked_AndPostingUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Issue/{slipId}"), "過帳發料");
            var posted = await CaptureAsync(slipId);
            Assert.False(posted.IsEmpty, $"前置條件不成立：過帳後應留下足跡，實際 {posted}");
            var qtyBefore = await ReadWorkOrderMaterialQtyAsync(woId);

            var raw = await PostFormAsync(client, token, $"{Route}/Save", new Dictionary<string, string>
            {
                ["SlipId"]        = slipId,
                ["SlipNo"]        = slipId,
                ["Category"]      = nameof(MaterialSlipCategory.InternalIssue),
                ["Status"]        = nameof(MaterialSlipStatus.Posted),
                ["SlipDate"]      = MfgPostDate.ToString("yyyy-MM-dd"),
                ["SourceDocType"] = "WorkOrder",
                ["SourceDocId"]   = woId,
                ["Remarks"]       = "改掉已過帳的單",
            });

            // HtmlDecode 不可省：Razor 預設 encoder 把 CJK 輸出成數值字元參照
            // （非草稿 → &#x975E;&#x8349;&#x7A3F;），直接比中文永遠不會中。
            var body = System.Net.WebUtility.HtmlDecode(raw);
            Assert.True(
                body.Contains("非草稿") || body.Contains("not in Draft", StringComparison.OrdinalIgnoreCase),
                "修改已過帳領料單：預期被守門擋下並回「非草稿狀態」，實際回應未含該訊息。\n"
                + "回應開頭：" + (body.Length > 500 ? body[..500] : body));

            await AssertUnchangedAsync(posted, slipId, "修改被擋後");
            Assert.Equal(qtyBefore, await ReadWorkOrderMaterialQtyAsync(woId));

            using var _ = NewScope(out var db);
            var remarks = await db.MaterialSlips.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SlipId == slipId).Select(s => s.Remarks).FirstAsync();
            Assert.NotEqual("改掉已過帳的單", remarks);
        }

        /// <summary>回傳整個 response body（Save 回 PartialView，JSON 解析不到東西）。</summary>
        private static async Task<string> PostFormAsync(
            HttpClient client, string token, string url, IDictionary<string, string> form)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(form);
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            var resp = await client.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
    }
}
