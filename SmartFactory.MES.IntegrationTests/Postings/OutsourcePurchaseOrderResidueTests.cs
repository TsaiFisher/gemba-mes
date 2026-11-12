using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 委外採購單（OutsourcePurchaseOrder）的刪除殘留守門——**本單據原本零覆蓋**。
    ///
    /// 這張單一筆庫存異動、一張傳票都不會產生，<see cref="PostingFootprint"/> 對它永遠是空的，
    /// 所以整組測試釘的全是**第三種殘留：上下游欄位懸空**。這類殘留最難查，
    /// 因為帳面完全正確，壞掉的是別張單據上的一個數字。
    ///
    /// 刪除時要回沖兩個方向，兩個都不在過帳足跡裡：
    ///   ① <c>WorkOrder</c> 的三個委外欄位（整品委外回填）——原本就有清。
    ///   ② <c>OutsourceDemand.TransferredQty</c>（已轉委外量）——**原本沒有回沖，是本批次找到的缺陷**。
    ///
    /// ② 的後果不是數字難看而是死結，且同時卡住兩處：
    ///   ‧ 轉委外清單只列 <c>TransferredQty &lt; DemandQty</c>，這筆建議直接從清單消失，
    ///     再也轉不出新的委外採購單——但料根本沒訂出去，產線在等一張不存在的單。
    ///   ‧ MrpEngine 把 <c>TransferredQty &gt; 0</c> 判為「已轉委外＝對外承諾」並 Blocked 擋下 MRP 重跑，
    ///     理由指向一張已經不存在的採購單，而**沒有任何畫面能解除這個阻擋**。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class OutsourcePurchaseOrderResidueTests : MfgDocResidueTestBase
    {
        public OutsourcePurchaseOrderResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        private const string Route = "/OutsourcePurchaseOrder";

        /// <summary>建立「工單鏈 + 委外建議 + 草稿委外採購單」，回傳三個單號。</summary>
        private async Task<(string OpoId, string DemandId, string WorkOrderId)> SeedAsync(string caseName)
        {
            var opoId = MfgDocNoFor(caseName, "OP");
            var woId  = opoId + "-WO";
            using var _ = NewScope(out var db);

            await ClearOutsourcePoAsync(db, opoId);
            await EnsureMfgMastersAsync(db);
            await SeedWorkOrderChainAsync(db, woId, MfgLineQty);
            // 已轉量＝本單數量：模擬 Convert 剛把這筆建議整筆轉出去的狀態。
            await SeedDraftOutsourcePoAsync(db, opoId, woId, MfgLineQty, transferredQty: MfgLineQty);

            return (opoId, opoId + "-D", woId);
        }

        private async Task<bool> OpoExistsAsync(string opoId)
        {
            using var _ = NewScope(out var db);
            return await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                .AnyAsync(o => o.OutsourcePurchaseOrderId == opoId);
        }

        private async Task SetStatusAsync(string opoId, OutsourcePoStatus status)
        {
            using var _ = NewScope(out var db);
            var po = await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(o => o.OutsourcePurchaseOrderId == opoId);
            po.Status = status;
            await db.SaveChangesAsync();
        }

        // ── 刪除回沖 ────────────────────────────────────────────────────────

        /// <summary>
        /// 刪除草稿委外採購單，必須把委外建議的已轉量減回去。
        ///
        /// 這是本批次找到的缺陷：原本只清工單的委外欄位，<c>TransferredQty</c> 留在原地。
        /// 建議因此永久停在「已轉委外」——轉單清單看不到它、MRP 重跑被它擋著，
        /// 而它指向的那張採購單已經不存在。沒有任何畫面能修好這個狀態。
        /// </summary>
        [Fact]
        public async Task DraftDelete_RestoresDemandTransferredQty()
        {
            var (opoId, demandId, _) = await SeedAsync(nameof(DraftDelete_RestoresDemandTransferredQty));
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(MfgLineQty, await ReadDemandTransferredQtyAsync(demandId));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{opoId}"),
                "刪除草稿委外採購單");

            Assert.False(await OpoExistsAsync(opoId), "刪除回報成功，但單據仍在");
            Assert.Equal(0m, await ReadDemandTransferredQtyAsync(demandId));
        }

        /// <summary>
        /// 回沖有下限 0：已轉量若已被減到 0（資料漂移或先前重複回沖），再刪一次不得變成負數。
        /// 負的已轉量會讓剩餘可轉量（DemandQty − TransferredQty）算得比需求量還大，
        /// 多出來的額度可以一路轉成委外採購單。與全庫其他回沖同一寫法。
        /// </summary>
        [Fact]
        public async Task DraftDelete_DemandReversal_FloorsAtZero()
        {
            var (opoId, demandId, _) = await SeedAsync(nameof(DraftDelete_DemandReversal_FloorsAtZero));
            using (var _ = NewScope(out var db))
            {
                var d = await db.OutsourceDemands.IgnoreQueryFilters().FirstAsync(x => x.DemandId == demandId);
                d.TransferredQty = 0m;
                await db.SaveChangesAsync();
            }
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{opoId}"),
                "刪除草稿委外採購單");

            var transferred = await ReadDemandTransferredQtyAsync(demandId);
            Assert.Equal(0m, transferred);
            Assert.True(transferred >= 0m, $"已轉委外量不得為負，實際 {transferred}");
        }

        /// <summary>刪除草稿委外採購單，必須清掉整品委外工單的回填欄位。</summary>
        [Fact]
        public async Task DraftDelete_ClearsWorkOrderOutsourceLink()
        {
            var (opoId, _, woId) = await SeedAsync(nameof(DraftDelete_ClearsWorkOrderOutsourceLink));
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(opoId, await ReadWorkOrderOutsourcePoIdAsync(woId));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{opoId}"),
                "刪除草稿委外採購單");

            Assert.Null(await ReadWorkOrderOutsourcePoIdAsync(woId));
        }

        // ── 刪除守門 ────────────────────────────────────────────────────────

        /// <summary>
        /// 已確認（送交廠商）的委外採購單不可刪，且工單回填與建議已轉量都不得被動到。
        ///
        /// 這道守門同時是下游收貨單的唯一保護：委外收貨單的
        /// <c>OutsourcePurchaseOrderId</c> **沒有外鍵**，收貨畫面也只列 Confirmed 的委外單。
        /// 「Confirmed 不可刪」因此是唯一擋住收貨單變成孤兒的東西——
        /// 若這格被放寬，已收貨、已開應付的單會指向一張查不到的採購單，
        /// 而資料庫不會發出任何聲音。
        /// </summary>
        [Fact]
        public async Task ConfirmedOpo_Delete_Blocked_AndUpstreamUntouched()
        {
            var (opoId, demandId, woId) = await SeedAsync(nameof(ConfirmedOpo_Delete_Blocked_AndUpstreamUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{opoId}"), "確認委外採購單");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{opoId}"),
                "刪除已確認委外採購單");

            Assert.True(await OpoExistsAsync(opoId), "刪除被擋下了，但單據本體卻不見了");
            Assert.Equal(MfgLineQty, await ReadDemandTransferredQtyAsync(demandId));
            Assert.Equal(opoId, await ReadWorkOrderOutsourcePoIdAsync(woId));
        }

        /// <summary>
        /// 作廢的委外採購單同樣不可刪。
        ///
        /// 與「已確認不可刪」不是重複：Void 是 Confirmed 之後的終點狀態，
        /// 若守門只看 Confirmed，「確認 → 作廢 → 刪除」三步就能繞過去，
        /// 而下游收貨單早已建立。狀態機的每個非草稿格子都要各自擋住。
        /// </summary>
        [Fact]
        public async Task VoidOpo_Delete_Blocked()
        {
            var (opoId, demandId, _) = await SeedAsync(nameof(VoidOpo_Delete_Blocked));
            await SetStatusAsync(opoId, OutsourcePoStatus.Void);
            var (client, token) = await NewAuthedClientAsync();

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/DeleteConfirmed/{opoId}"),
                "刪除已作廢委外採購單");

            Assert.True(await OpoExistsAsync(opoId), "刪除被擋下了，但單據本體卻不見了");
            Assert.Equal(MfgLineQty, await ReadDemandTransferredQtyAsync(demandId));
        }

        /// <summary>
        /// 重複確認不得改變任何上游數字。委外採購單的 Confirm 不碰庫存，
        /// 但它是「對外承諾」的分界點——確認兩次若讓已轉量再加一次，
        /// 建議會被灌成超額轉出。
        /// </summary>
        [Fact]
        public async Task ConfirmTwice_DoesNotTouchUpstream()
        {
            var (opoId, demandId, woId) = await SeedAsync(nameof(ConfirmTwice_DoesNotTouchUpstream));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{opoId}"), "第一次確認");
            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Confirm/{opoId}"), "重複確認");

            Assert.Equal(MfgLineQty, await ReadDemandTransferredQtyAsync(demandId));
            Assert.Equal(opoId, await ReadWorkOrderOutsourcePoIdAsync(woId));
        }
    }
}
