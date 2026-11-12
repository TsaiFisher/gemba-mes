using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 「中途反悔」：下游已經過帳之後，回頭動上游單據。
    ///
    /// 與 Phase 1 那批單據級守門測試差在哪裡：
    ///   ・Phase 1 是**合成狀態**——直接把 <c>ReceivedQty = 5</c> 寫進種子，再試刪除。
    ///     驗的是「守門看到那個數字會不會擋」。
    ///   ・本檔是**真的把鏈跑出來**——建下游單、經 HTTP 過帳、產生庫存異動／傳票／發票，
    ///     然後才回頭動上游。驗的是「上游被擋下的同時，下游那些已經產生的帳一格都沒動」。
    ///
    /// 為什麼這個差別重要：守門擋下與「擋下之前已經先改了東西」在畫面上完全一樣，
    /// 都是一句錯誤訊息。差別只在別張單據上的數字，以及庫存與帳務裡的那幾筆。
    /// 「先回沖、後檢查」這種順序錯誤，只有在下游真的有帳的時候才會露出來——
    /// 合成種子驗不到，因為根本沒有帳可以被動到。
    ///
    /// 四條流程各挑一個反悔動作，刻意不重複 Phase 1 已覆蓋的格子：
    ///   ① P2P：進貨已過帳 → 刪採購單（HTTP）＋改採購單（服務層，守門在服務不在 Controller）
    ///   ② O2C：出貨已過帳並已開應收 → 刪銷貨訂單
    ///   ③ MFG：領料已過帳 → 作廢工單（工單沒有刪除，作廢是唯一的反悔出口）
    ///   ④ 委外：委外收貨已過帳並已開應付 → 刪委外採購單
    ///
    /// 掛 "ScheduleIntegration" 的理由同 <see cref="PostingResidueTestBase"/>：
    /// 那是唯一帶 TestAuthHandler 的已登入 collection，新增第三個會觸發測試庫的
    /// migration／種子雙 23505 競態。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MidFlowReversalTests : MfgDocResidueTestBase
    {
        private const string Customer = Prefix + "-RVCUS";

        public MidFlowReversalTests(AuthedScheduleFactory factory) : base(factory)
            => SetSimplifiedMode("true");

        /// <summary>
        /// inv_simplified_mode 是全域參數，跑完一律清回未設定，否則會污染同 collection
        /// 的其他測試（序列執行，清乾淨就不會互撞）。
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

        // ════════════════════════════════════════════════════════════════════
        // ① P2P：進貨已過帳 → 動採購單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 進貨單已過帳（庫存＋傳票＋應付都已產生）之後，採購單既不可刪也不可改，
        /// 且兩次被擋都不得動到進貨單那邊的任何一筆帳。
        ///
        /// 刪除與修改的守門寫在同一個地方（<c>PurchaseService</c> 兩支方法各判一次
        /// <c>ReceivedQty &gt; 0</c>），但**修改那條沒有經過 Controller**——
        /// <c>UpdatePurchaseOrderAsync</c> 是服務層方法，Controller 只是轉呼叫。
        /// 故刪除走 HTTP、修改直接呼叫服務：走 HTTP 反而會因為表單模型驗證先擋下來，
        /// 得到一支「看起來綠、其實沒碰到守門」的測試。
        /// </summary>
        [Fact]
        public async Task PostedReceipt_ThenReversePurchaseOrder_BlockedAndDownstreamUntouched()
        {
            var (poNo, grNo, prNo) = await SeedPurchaseChainAsync(
                nameof(PostedReceipt_ThenReversePurchaseOrder_BlockedAndDownstreamUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/GoodsReceipt/Confirm/{grNo}"), "確認進貨過帳");

            var posted = await CaptureAsync(grNo);
            Assert.True(posted.MovementLines > 0, $"前置條件不成立：進貨應留下庫存異動，實際 {posted}");
            Assert.Equal(1, posted.ApInvoices);

            // ── 刪採購單 ────────────────────────────────────────────────────
            AssertBlocked(await PostActionAsync(client, token, $"/PurchaseOrder/Delete/{poNo}"),
                "刪除已有進貨的採購單");

            await AssertUnchangedAsync(posted, grNo, "採購單刪除被擋後");
            Assert.Equal(MfgLineQty, await ReadPoReceivedQtyAsync(poNo));
            // 刪除守門若排在 RestorePrOrderedQtyAsync 之後，這裡的已轉採購量會先被扣回去：
            // 採購單還在、進貨帳也都在，卻有一張請購單憑空多出可再轉採購的餘量。
            Assert.Equal(MfgLineQty * 2, await ReadPrOrderedQtyAsync(prNo));

            // ── 改採購單數量 ────────────────────────────────────────────────
            using (var scope = NewScope(out _))
            {
                var purchase = scope.ServiceProvider.GetRequiredService<IPurchaseService>();
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    purchase.UpdatePurchaseOrderAsync(new PurchaseOrderFormVm
                    {
                        PurchaseOrderId = poNo,
                        OrderDate       = MfgPostDate,
                        SupplierCode    = MfgSupplier,
                        Lines =
                        [
                            new PurchaseOrderLineVm
                            {
                                LineNo = 1, ProductCode = MfgProduct,
                                OrderQty = 1m, UomCode = MfgUom,
                                StockQty = 1m, StockUomCode = MfgUom,
                                UnitPrice = MfgUnitCost, CurrencyCode = "TWD", ExchangeRate = 1m,
                            },
                        ],
                    }, "admin"));
            }

            await AssertUnchangedAsync(posted, grNo, "採購單修改被擋後");
            // 守門若寫成「先回沖 PR、後檢查」，這裡的訂購量會已經被改掉。
            Assert.Equal(MfgLineQty * 2, await ReadPoOrderQtyAsync(poNo));
            Assert.Equal(MfgLineQty, await ReadPoReceivedQtyAsync(poNo));
            Assert.Equal(MfgLineQty * 2, await ReadPrOrderedQtyAsync(prNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // ② O2C：出貨已過帳 → 刪銷貨訂單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 出貨單已過帳並已開立應收之後，銷貨訂單不可刪，且應收發票不得受影響。
        ///
        /// Phase 1 已經測過「已出貨的訂單不可刪」，但那是把 <c>ShippedQty</c> 直接寫進種子——
        /// 當時沒有出貨單、沒有異動、更沒有應收發票，所以「擋下時帳有沒有被動到」根本無從驗起。
        /// 這裡補的就是那一段：應收發票對訂單沒有外鍵（靠 <c>SourceDocId</c> 字串），
        /// 訂單若被刪掉，發票會掛在一張查不到的訂單上，而資料庫不會發出任何聲音。
        /// </summary>
        [Fact]
        public async Task PostedShipment_ThenDeleteSalesOrder_BlockedAndArUntouched()
        {
            var (soNo, gsNo) = await SeedSalesChainAsync(
                nameof(PostedShipment_ThenDeleteSalesOrder_BlockedAndArUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/GoodsShipment/Confirm/{gsNo}"), "確認出貨過帳");

            var posted = await CaptureAsync(gsNo);
            Assert.True(posted.MovementLines > 0, $"前置條件不成立：出貨應留下庫存異動，實際 {posted}");
            Assert.Equal(1, posted.ArInvoices);

            AssertBlocked(await PostActionAsync(client, token, $"/SalesOrder/Delete/{soNo}"),
                "刪除已出貨的銷貨訂單");

            await AssertUnchangedAsync(posted, gsNo, "銷貨訂單刪除被擋後");
            Assert.Equal(MfgLineQty, await ReadSoShippedQtyAsync(soNo));
            Assert.True(await SoExistsAsync(soNo), "刪除被擋下了，但銷貨訂單卻不見了");
        }

        // ════════════════════════════════════════════════════════════════════
        // ③ MFG：領料已過帳 → 作廢工單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 領料單已過帳（料已實際離開倉庫）之後，工單不得直接作廢。
        ///
        /// 工單沒有刪除動作，作廢是唯一的反悔出口，而作廢目前只擋一件事：
        /// 派工有沒有回報產出（<c>Dispatches.QtyDone &gt; 0</c>，mfg-domain.md §4.2）。
        /// 「料已經領出去了」不在守門範圍內——但那批料的庫存已經扣掉、
        /// <c>WorkOrderMaterial.IssuedQty</c> 已經回寫，而作廢之後**沒有任何東西會來承接它**：
        /// 工單不會結案，成本結算不會跑，那批料就停在「已領出、無工單消耗」的狀態。
        ///
        /// 生產領料不入總帳（形態轉換，見 BuildJournalLines 的帳務語意），
        /// 所以這個缺口不會讓試算表不平——它只在數量面留下無主的已領料，
        /// 帳上完全安靜，這正是最難查的那一類。
        /// </summary>
        [Fact]
        public async Task IssuedMaterial_ThenVoidWorkOrder_BlockedAndIssuedQtyUntouched()
        {
            var (slipId, woId) = await SeedIssueSlipChainAsync(
                nameof(IssuedMaterial_ThenVoidWorkOrder_BlockedAndIssuedQtyUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/MaterialSlip/Issue/{slipId}"), "發料過帳");

            var posted = await CaptureAsync(slipId);
            Assert.True(posted.MovementLines > 0, $"前置條件不成立：發料應留下庫存異動，實際 {posted}");
            var (issuedBefore, _) = await ReadWorkOrderMaterialQtyAsync(woId);
            Assert.Equal(MfgLineQty, issuedBefore);

            AssertBlocked(await PostActionAsync(client, token, $"/WorkOrder/Void/{woId}"),
                "作廢已領料的工單");

            await AssertUnchangedAsync(posted, slipId, "工單作廢被擋後");
            var (issuedAfter, _) = await ReadWorkOrderMaterialQtyAsync(woId);
            Assert.Equal(issuedBefore, issuedAfter);
            Assert.NotEqual(WorkOrderStatus.Void, await ReadWorkOrderStatusAsync(woId));
        }

        /// <summary>
        /// 上一支的反向順序：領料單已確認、**還沒過帳**時先作廢工單，再回頭過帳那張領料單。
        ///
        /// 作廢守門只看「已過帳未退」的料（IssuedQty − ReturnedQty），未過帳的領料單不算；
        /// 而 Confirmed 的領料單不能取消、只能過帳。兩者疊起來，料就會發到一張作廢的工單上——
        /// 正是作廢守門註解裡說要擋的「已領出、無工單消耗」。
        /// 不變量：工單 Void 與其領料單 Posted 不得同時成立（擋在哪一步都可以）。
        /// </summary>
        [Fact]
        public async Task ConfirmedSlip_VoidWorkOrderThenIssue_NeverPostsOntoVoidWorkOrder()
        {
            var (slipId, woId) = await SeedIssueSlipChainAsync(
                nameof(ConfirmedSlip_VoidWorkOrderThenIssue_NeverPostsOntoVoidWorkOrder));
            var (client, token) = await NewAuthedClientAsync();

            var voided = await PostActionAsync(client, token, $"/WorkOrder/Void/{woId}");
            var issued = await PostActionAsync(client, token, $"/MaterialSlip/Issue/{slipId}");

            var woStatus = await ReadWorkOrderStatusAsync(woId);
            var (issuedQty, _) = await ReadWorkOrderMaterialQtyAsync(woId);
            Assert.False(woStatus == WorkOrderStatus.Void && issued.Success,
                $"料發到作廢工單上：作廢={voided}，發料={issued}，工單用料 IssuedQty={issuedQty}");

            // 擋在作廢那一步：倉庫已確認揀料的單不能取消，工單只能等料過帳、退回後再作廢
            AssertBlocked(voided, "有已確認領料單時作廢工單");
            Assert.Contains(slipId, voided.Message);
            AssertAllowed(issued, "工單未作廢時照常發料");
        }

        /// <summary>
        /// 過帳端的第二道防護：工單已是 Void（作廢守門加上之前就作廢的舊資料、或任何繞道），
        /// 殘留的領料單與委外收貨都不得過帳，且不留任何足跡。
        /// </summary>
        [Fact]
        public async Task AlreadyVoidWorkOrder_IssueAndOutsourceReceipt_BothBlockedAtPosting()
        {
            var (slipId, slipWo) = await SeedIssueSlipChainAsync(
                nameof(AlreadyVoidWorkOrder_IssueAndOutsourceReceipt_BothBlockedAtPosting));
            var (_, _, receiptId) = await SeedOutsourceChainAsync(
                nameof(AlreadyVoidWorkOrder_IssueAndOutsourceReceipt_BothBlockedAtPosting));
            var opoWo = MfgDocNoFor(nameof(AlreadyVoidWorkOrder_IssueAndOutsourceReceipt_BothBlockedAtPosting), "OP") + "-WO";
            using (NewScope(out var db))
                await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == slipWo || w.WorkOrderId == opoWo)
                    .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WorkOrderStatus.Void));
            var (client, token) = await NewAuthedClientAsync();

            var issued = await PostActionAsync(client, token, $"/MaterialSlip/Issue/{slipId}");
            AssertBlocked(issued, "發料到作廢工單");
            Assert.Contains(slipWo, issued.Message);
            Assert.True((await CaptureAsync(slipId)).IsEmpty, "被擋的發料不該留足跡");

            var received = await PostActionAsync(client, token, $"/OutsourceReceipt/Confirm/{receiptId}");
            AssertBlocked(received, "委外收貨到作廢工單");
            Assert.True((await CaptureAsync(receiptId)).IsEmpty, "被擋的委外收貨不該留足跡");
        }

        /// <summary>已結案工單只收退料（A7）：發料過帳被擋、不留足跡。</summary>
        [Fact]
        public async Task ClosedWorkOrder_Issue_BlockedAtPosting()
        {
            var (slipId, woId) = await SeedIssueSlipChainAsync(nameof(ClosedWorkOrder_Issue_BlockedAtPosting));
            using (NewScope(out var db))
                await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == woId)
                    .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WorkOrderStatus.Closed));
            var (client, token) = await NewAuthedClientAsync();

            var issued = await PostActionAsync(client, token, $"/MaterialSlip/Issue/{slipId}");
            AssertBlocked(issued, "發料到已結案工單");
            Assert.Contains(woId, issued.Message);
            Assert.True((await CaptureAsync(slipId)).IsEmpty, "被擋的發料不該留足跡");
        }

        /// <summary>
        /// 可取消的領料單（草稿～揀料中，尚無庫存異動）隨工單作廢一併取消；
        /// 投產自動建的 Push 草稿領料單就是這種，不能因它擋下作廢。
        /// </summary>
        [Fact]
        public async Task DraftSlip_VoidWorkOrder_CancelsSlipAndBlocksLaterIssue()
        {
            var (slipId, woId) = await SeedIssueSlipChainAsync(
                nameof(DraftSlip_VoidWorkOrder_CancelsSlipAndBlocksLaterIssue));
            using (NewScope(out var db))
                await db.MaterialSlips.IgnoreQueryFilters().Where(s => s.SlipId == slipId)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, MaterialSlipStatus.Draft));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/WorkOrder/Void/{woId}"), "作廢只有草稿領料單的工單");

            Assert.Equal(WorkOrderStatus.Void, await ReadWorkOrderStatusAsync(woId));
            using (NewScope(out var db))
                Assert.Equal(MaterialSlipStatus.Cancelled, await db.MaterialSlips.IgnoreQueryFilters()
                    .Where(s => s.SlipId == slipId).Select(s => s.Status).FirstAsync());
            Assert.True((await CaptureAsync(slipId)).IsEmpty, "取消的領料單不該有過帳足跡");
        }

        /// <summary>
        /// 委外版本：委外採購單已確認、收貨單草稿時先作廢工單，再確認收貨。
        /// 作廢只取消派工與預留，不碰委外採購單；收貨過帳也不看工單狀態。
        /// 不變量：工單 Void 與其委外收貨過帳不得同時成立。
        /// </summary>
        [Fact]
        public async Task ConfirmedOpo_VoidWorkOrderThenReceive_NeverPostsOntoVoidWorkOrder()
        {
            var (_, _, receiptId) = await SeedOutsourceChainAsync(
                nameof(ConfirmedOpo_VoidWorkOrderThenReceive_NeverPostsOntoVoidWorkOrder));
            var woId = MfgDocNoFor(nameof(ConfirmedOpo_VoidWorkOrderThenReceive_NeverPostsOntoVoidWorkOrder), "OP") + "-WO";
            var (client, token) = await NewAuthedClientAsync();

            var voided   = await PostActionAsync(client, token, $"/WorkOrder/Void/{woId}");
            var received = await PostActionAsync(client, token, $"/OutsourceReceipt/Confirm/{receiptId}");

            var woStatus = await ReadWorkOrderStatusAsync(woId);
            Assert.False(woStatus == WorkOrderStatus.Void && received.Success,
                $"委外收貨過帳到作廢工單上：作廢={voided}，收貨={received}");

            AssertBlocked(voided, "有已確認委外採購單時作廢工單");
            Assert.NotEqual(WorkOrderStatus.Void, woStatus);
        }

        /// <summary>
        /// 已確認委外採購單的作廢出口（A5）：有收貨單（含草稿）擋、料還在廠商端擋；
        /// 兩者都清掉後作廢成功、委外建議已轉量回沖，來源工單也就能作廢了。
        /// </summary>
        [Fact]
        public async Task ConfirmedOpo_Void_BlockedByReceiptAndMaterialAtVendor_ThenUnblocksWorkOrderVoid()
        {
            const string Case = nameof(ConfirmedOpo_Void_BlockedByReceiptAndMaterialAtVendor_ThenUnblocksWorkOrderVoid);
            var (opoId, demandId, receiptId) = await SeedOutsourceChainAsync(Case);
            var woId = MfgDocNoFor(Case, "OP") + "-WO";
            var osSlip = opoId + "-OI";
            var (client, token) = await NewAuthedClientAsync();

            // ① 有草稿收貨單 → 擋
            var r1 = await PostActionAsync(client, token, $"/OutsourcePurchaseOrder/Void/{opoId}");
            AssertBlocked(r1, "有收貨單時作廢委外採購單");
            Assert.Contains(receiptId, r1.Message);

            using (NewScope(out var db))
            {
                await db.OutsourceReceiptLines.IgnoreQueryFilters().Where(l => l.OutsourceReceiptId == receiptId).ExecuteDeleteAsync();
                await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.OutsourceReceiptId == receiptId).ExecuteDeleteAsync();
                await db.MaterialSlipLines.IgnoreQueryFilters().Where(l => l.SlipId == osSlip).ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters().Where(x => x.SlipId == osSlip).ExecuteDeleteAsync();
                // 已過帳的委外發料＝料在廠商端
                db.MaterialSlips.Add(new MaterialSlip
                {
                    SlipId = osSlip, SlipNo = osSlip, Category = MaterialSlipCategory.OutsourceIssue,
                    Status = MaterialSlipStatus.Posted, SlipDate = MfgPostDate,
                    SourceDocType = "WorkOrder", SourceDocId = woId, PlantId = Plant,
                    Lines = { new MaterialSlipLine { SlipId = osSlip, LineNo = 1, ProductCode = MfgMaterial,
                        RequestedQty = 3m, ConfirmedQty = 3m, UomCode = MfgUom, LotNo = "" } },
                });
                await db.SaveChangesAsync();
            }

            // ② 料在廠商端 → 擋
            var r2 = await PostActionAsync(client, token, $"/OutsourcePurchaseOrder/Void/{opoId}");
            AssertBlocked(r2, "料在廠商端時作廢委外採購單");
            Assert.Contains(MfgMaterial, r2.Message);

            // ③ 料已退回（委外退料同量）→ 作廢成功、已轉量回沖、工單可作廢
            using (NewScope(out var db))
            {
                db.MaterialSlips.Add(new MaterialSlip
                {
                    SlipId = osSlip + "R", SlipNo = osSlip + "R", Category = MaterialSlipCategory.OutsourceReturn,
                    Status = MaterialSlipStatus.Posted, SlipDate = MfgPostDate,
                    SourceDocType = "WorkOrder", SourceDocId = woId, PlantId = Plant,
                    Lines = { new MaterialSlipLine { SlipId = osSlip + "R", LineNo = 1, ProductCode = MfgMaterial,
                        RequestedQty = 3m, ConfirmedQty = 3m, UomCode = MfgUom, LotNo = "" } },
                });
                await db.SaveChangesAsync();
            }
            try
            {
                AssertAllowed(await PostActionAsync(client, token, $"/OutsourcePurchaseOrder/Void/{opoId}"), "作廢委外採購單");
                using (NewScope(out var db))
                    Assert.Equal(OutsourcePoStatus.Void, await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                        .Where(o => o.OutsourcePurchaseOrderId == opoId).Select(o => o.Status).FirstAsync());
                Assert.Equal(0m, await ReadDemandTransferredQtyAsync(demandId));

                AssertAllowed(await PostActionAsync(client, token, $"/WorkOrder/Void/{woId}"), "委外單作廢後作廢工單");
            }
            finally
            {
                using var _ = NewScope(out var db);
                await db.MaterialSlipLines.IgnoreQueryFilters().Where(l => l.SlipId == osSlip || l.SlipId == osSlip + "R").ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters().Where(x => x.SlipId == osSlip || x.SlipId == osSlip + "R").ExecuteDeleteAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ④ 委外：委外收貨已過帳 → 刪委外採購單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 委外收貨已過帳並已開立應付之後，委外採購單不可刪，收貨那邊的帳也不得被動到。
        ///
        /// 這道守門是下游收貨單**唯一**的保護：<c>OutsourceReceipt.OutsourcePurchaseOrderId</c>
        /// 沒有外鍵，收貨畫面也只列 Confirmed 的委外單。守門若被放寬，
        /// 已收貨、已開應付的單會指向一張查不到的採購單，而資料庫不會攔。
        ///
        /// Phase 1 測過「Confirmed 的委外採購單不可刪」，但當時下游一張收貨單都沒有；
        /// 這裡補的是「下游真的有帳時，擋下的同時那些帳一格不動」。
        /// </summary>
        [Fact]
        public async Task PostedOutsourceReceipt_ThenDeleteOpo_BlockedAndDownstreamUntouched()
        {
            var (opoId, demandId, receiptId) = await SeedOutsourceChainAsync(
                nameof(PostedOutsourceReceipt_ThenDeleteOpo_BlockedAndDownstreamUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/OutsourceReceipt/Confirm/{receiptId}"),
                "確認委外收貨過帳");

            var posted = await CaptureAsync(receiptId);
            Assert.True(posted.MovementLines > 0, $"前置條件不成立：委外收貨應留下庫存異動，實際 {posted}");

            AssertBlocked(await PostActionAsync(client, token, $"/OutsourcePurchaseOrder/DeleteConfirmed/{opoId}"),
                "刪除已收貨的委外採購單");

            await AssertUnchangedAsync(posted, receiptId, "委外採購單刪除被擋後");
            Assert.Equal(MfgLineQty, await ReadDemandTransferredQtyAsync(demandId));
            Assert.Equal(opoId, await ReadReceiptOpoIdAsync(receiptId));
        }

        // ════════════════════════════════════════════════════════════════════
        // 種子
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 請購單 → 採購單（訂購 2 倍量、已收 1 倍量）＋ 一張已核准未過帳的進貨單。
        ///
        /// 採購單刻意由請購單轉來（<c>PurchaseOrderLine.PurchaseRequestId/PrLineNo</c> 有值），
        /// 因為刪除／修改採購單在守門通過後做的第一件事就是
        /// <c>RestorePrOrderedQtyAsync</c>——把請購單的已轉採購量扣回去。
        /// 沒有這條上游鏈的話，「先回沖、後檢查」這個順序錯誤在本測試裡根本無從發生，
        /// 那條看門狗斷言就會永遠是綠的（而且是因為沒東西可壞，不是因為守得住）。
        /// </summary>
        private async Task<(string PoNo, string GrNo, string PrNo)> SeedPurchaseChainAsync(string caseName)
        {
            var grNo = MfgDocNoFor(caseName, "PE");
            var poNo = grNo + "-PO";
            var prNo = grNo + "-PR";
            using var _ = NewScope(out var db);

            await ClearDocPostingsAsync(db, grNo);
            await db.GoodsReceiptLines.IgnoreQueryFilters().Where(l => l.GoodsReceiptId == grNo).ExecuteDeleteAsync();
            await db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.GoodsReceiptId == grNo).ExecuteDeleteAsync();
            await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters().Where(o => o.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseRequestLines.IgnoreQueryFilters().Where(l => l.PurchaseRequestId == prNo).ExecuteDeleteAsync();
            await db.PurchaseRequests.IgnoreQueryFilters().Where(r => r.PurchaseRequestId == prNo).ExecuteDeleteAsync();

            await EnsureMfgMastersAsync(db);

            db.PurchaseRequests.Add(new PurchaseRequest
            {
                PurchaseRequestId = prNo, RequestDate = MfgPostDate, RequestedBy = "admin",
                PlantId = Plant, CreatedByRoleId = RoleId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new PurchaseRequestLine
                    {
                        PurchaseRequestId = prNo, LineNo = 1, ProductCode = MfgProduct,
                        RequestQty = MfgLineQty * 2, StockQty = MfgLineQty * 2,
                        UomCode = MfgUom, StockUomCode = MfgUom,
                        // 已全數轉為採購單。
                        OrderedQty = MfgLineQty * 2,
                    },
                },
            });

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                PurchaseOrderId = poNo, OrderDate = MfgPostDate, SupplierCode = MfgSupplier,
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new PurchaseOrderLine
                    {
                        PurchaseOrderId = poNo, LineNo = 1, ProductCode = MfgProduct,
                        PurchaseRequestId = prNo, PrLineNo = 1,
                        OrderQty = MfgLineQty * 2, StockQty = MfgLineQty * 2,
                        UomCode = MfgUom, StockUomCode = MfgUom, UnitPrice = MfgUnitCost,
                        // 進貨單建立時上游已累加過收貨量。
                        ReceivedQty = MfgLineQty,
                    },
                },
            });

            db.GoodsReceipts.Add(new GoodsReceipt
            {
                GoodsReceiptId = grNo, ReceiptDate = MfgPostDate, SupplierCode = MfgSupplier,
                TypeCode = "PI", TypeCategory = "Purchase",
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                TotalAmount = MfgLineQty * MfgUnitCost,
                Lines = new List<GoodsReceiptLine>
                {
                    new()
                    {
                        GoodsReceiptId = grNo, LineNo = 1, ProductCode = MfgProduct,
                        PurchaseOrderId = poNo, PoLineNo = 1,
                        ReceiptQty = MfgLineQty, StockQty = MfgLineQty,
                        UomCode = MfgUom, StockUomCode = MfgUom,
                        UnitPrice = MfgUnitCost, ExchangeRate = 1m,
                        LineAmount = MfgLineQty * MfgUnitCost,
                        TotalAmount = MfgLineQty * MfgUnitCost,
                        AmountLocal = MfgLineQty * MfgUnitCost,
                        WhCode = MfgWh, LocationCode = MfgLoc, LotNo = "",
                        QtyPutAway = 0m, ReturnedQty = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return (poNo, grNo, prNo);
        }

        /// <summary>銷貨訂單（訂購 2 倍量、已出 1 倍量）＋ 一張已核准未過帳的出貨單。</summary>
        private async Task<(string SoNo, string GsNo)> SeedSalesChainAsync(string caseName)
        {
            var gsNo = MfgDocNoFor(caseName, "GS");
            var soNo = gsNo + "-SO";
            using var _ = NewScope(out var db);

            await ClearDocPostingsAsync(db, gsNo);
            await db.ARInvoiceLines.IgnoreQueryFilters()
                .Where(l => db.ARInvoices.IgnoreQueryFilters()
                    .Where(i => i.SourceDocId == gsNo).Select(i => i.ARInvoiceId).Contains(l.ARInvoiceId))
                .ExecuteDeleteAsync();
            await db.ARInvoices.IgnoreQueryFilters().Where(i => i.SourceDocId == gsNo).ExecuteDeleteAsync();
            await db.GoodsShipmentLines.IgnoreQueryFilters().Where(l => l.GoodsShipmentId == gsNo).ExecuteDeleteAsync();
            await db.GoodsShipments.IgnoreQueryFilters().Where(d => d.GoodsShipmentId == gsNo).ExecuteDeleteAsync();
            await db.SalesOrderLines.IgnoreQueryFilters().Where(l => l.SalesOrderId == soNo).ExecuteDeleteAsync();
            await db.SalesOrders.IgnoreQueryFilters().Where(o => o.SalesOrderId == soNo).ExecuteDeleteAsync();

            await EnsureMfgMastersAsync(db);
            await EnsureCustomerAsync(db);

            db.SalesOrders.Add(new SalesOrder
            {
                SalesOrderId = soNo, OrderDate = MfgPostDate, CompanyCode = Customer,
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new SalesOrderLine
                    {
                        SalesOrderId = soNo, LineNo = 1, ProductCode = MfgProduct,
                        OrderQty = MfgLineQty * 2, StockQty = MfgLineQty * 2,
                        UomCode = MfgUom, StockUomCode = MfgUom, UnitPrice = MfgUnitCost,
                        // 出貨單建立時上游已累加過出貨量。
                        ShippedQty = MfgLineQty,
                    },
                },
            });

            db.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = gsNo, ShipDate = MfgPostDate, CompanyCode = Customer,
                SalesOrderId = soNo, TypeCode = "GS",
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                Lines = new List<GoodsShipmentLine>
                {
                    new()
                    {
                        GoodsShipmentId = gsNo, LineNo = 1, ProductCode = MfgProduct,
                        SalesOrderId = soNo, SoLineNo = 1,
                        ShipQty = MfgLineQty, StockQty = MfgLineQty,
                        UomCode = MfgUom, StockUomCode = MfgUom,
                        UnitPrice = MfgUnitCost, ExchangeRate = 1m,
                        LineAmount = MfgLineQty * MfgUnitCost,
                        TotalAmount = MfgLineQty * MfgUnitCost,
                        AmountLocal = MfgLineQty * MfgUnitCost,
                        WhCode = MfgWh, LocationCode = MfgLoc, LotNo = "",
                        QtyPutAway = 0m, ReturnedQty = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return (soNo, gsNo);
        }

        /// <summary>工單鏈 ＋ 一張 Confirmed 領料單（過帳動作是 Issue，不是 Confirm）。</summary>
        private async Task<(string SlipId, string WorkOrderId)> SeedIssueSlipChainAsync(string caseName)
        {
            var slipId = MfgDocNoFor(caseName, "MS");
            var woId   = slipId + "-WO";
            using var _ = NewScope(out var db);

            await ClearDocPostingsAsync(db, slipId);
            await db.MaterialSlipLines.IgnoreQueryFilters().Where(l => l.SlipId == slipId).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters().Where(s => s.SlipId == slipId).ExecuteDeleteAsync();

            await EnsureMfgMastersAsync(db);
            await SeedWorkOrderChainAsync(db, woId, MfgLineQty);

            db.MaterialSlips.Add(new MaterialSlip
            {
                SlipId = slipId, SlipNo = slipId,
                Category = MaterialSlipCategory.InternalIssue,
                Status = MaterialSlipStatus.Confirmed,
                SlipDate = MfgPostDate,
                SourceDocType = "WorkOrder", SourceDocId = woId,
                PlantId = Plant,
                Lines =
                {
                    new MaterialSlipLine
                    {
                        SlipId = slipId, LineNo = 1,
                        SourceLineId = "1_1",           // "{OperationSeq}_{BomSeq}"
                        ProductCode = MfgMaterial,
                        RequestedQty = MfgLineQty, ConfirmedQty = MfgLineQty,
                        UomCode = MfgUom,
                        FromWarehouseCode = MfgWh, FromLocationCode = MfgLoc,
                        LotNo = "",
                    },
                },
            });

            await db.SaveChangesAsync();
            return (slipId, woId);
        }

        /// <summary>工單鏈 ＋ Confirmed 委外採購單 ＋ 一張草稿委外收貨單。</summary>
        private async Task<(string OpoId, string DemandId, string ReceiptId)> SeedOutsourceChainAsync(string caseName)
        {
            var opoId     = MfgDocNoFor(caseName, "OP");
            var woId      = opoId + "-WO";
            var receiptId = opoId + "-RC";
            using var _ = NewScope(out var db);

            await ClearDocPostingsAsync(db, receiptId);
            await db.APInvoiceLines.IgnoreQueryFilters()
                .Where(l => db.APInvoices.IgnoreQueryFilters()
                    .Where(i => i.SourceDocId == receiptId).Select(i => i.APInvoiceId).Contains(l.APInvoiceId))
                .ExecuteDeleteAsync();
            await db.APInvoices.IgnoreQueryFilters().Where(i => i.SourceDocId == receiptId).ExecuteDeleteAsync();
            await db.OutsourceReceiptLines.IgnoreQueryFilters()
                .Where(l => l.OutsourceReceiptId == receiptId).ExecuteDeleteAsync();
            await db.OutsourceReceipts.IgnoreQueryFilters()
                .Where(r => r.OutsourceReceiptId == receiptId).ExecuteDeleteAsync();
            await ClearOutsourcePoAsync(db, opoId);

            await EnsureMfgMastersAsync(db);
            await SeedWorkOrderChainAsync(db, woId, MfgLineQty);
            var (_, demandId) = await SeedDraftOutsourcePoAsync(db, opoId, woId, MfgLineQty, MfgLineQty);

            // 收貨畫面只列 Confirmed 的委外採購單，且「Confirmed 不可刪」正是要驗的那道守門。
            var opo = await db.OutsourcePurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(o => o.OutsourcePurchaseOrderId == opoId);
            opo.Status = OutsourcePoStatus.Confirmed;

            db.OutsourceReceipts.Add(new OutsourceReceipt
            {
                OutsourceReceiptId = receiptId,
                OutsourcePurchaseOrderId = opoId,
                ReceiptDate = MfgPostDate,
                SupplierCode = MfgSupplier,
                TypeCode = "OI", TypeCategory = "Purchase",
                Status = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "admin", PlantId = Plant, CreatedByRoleId = RoleId,
                Lines =
                {
                    new OutsourceReceiptLine
                    {
                        OutsourceReceiptId = receiptId, LineNo = 1,
                        OpoLineNo = 1,
                        ProductCode = MfgProduct,
                        ReceiptQty = MfgLineQty, StockQty = MfgLineQty,
                        UomCode = MfgUom, StockUomCode = MfgUom,
                        UnitCost = MfgUnitCost, ExchangeRate = 1m,
                        LineAmount = MfgLineQty * MfgUnitCost,
                        TotalAmount = MfgLineQty * MfgUnitCost,
                        AmountLocal = MfgLineQty * MfgUnitCost,
                        WhCode = MfgWh, LocationCode = MfgLoc, LotNo = "",
                        QtyPutAway = 0m,
                        // 0 = 不罰扣：容許報廢率為 0 時 OutsourceScrapPenaltyService 直接 no-op。
                        // null ＝ 合約未約定損耗條款 → 不扣款（本測試不驗扣款，別讓它插進來）。
                        // 0 現在的意思是「明確約定零容忍」，會真的算出罰款。
                        AllowedScrapRate = null,
                    },
                },
            });

            await db.SaveChangesAsync();
            return (opoId, demandId, receiptId);
        }

        // ── 清理與讀取 ──────────────────────────────────────────────────────

        /// <summary>清掉單號底下的庫存異動與傳票（含後沖那種「單號-後綴」的異動）。</summary>
        private static async Task ClearDocPostingsAsync(ApplicationDbContext db, string docNo)
        {
            await db.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == docNo || m.DocNo.StartsWith(docNo + "-")).ExecuteDeleteAsync();

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

        private static async Task EnsureCustomerAsync(ApplicationDbContext db)
        {
            if (await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer)) return;
            db.Companies.Add(new Company
            {
                CompanyCode = Customer, CompanyName = "中途反悔測試客戶",
                IsSupplier = false, IsCustomer = true, IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        private async Task<decimal> ReadPoReceivedQtyAsync(string poNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseOrderId == poNo && l.LineNo == 1)
                .Select(l => l.ReceivedQty).FirstAsync();
        }

        /// <summary>請購單的已轉採購量——刪／改採購單通過守門後第一個被回沖的欄位。</summary>
        private async Task<decimal> ReadPrOrderedQtyAsync(string prNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseRequestLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseRequestId == prNo && l.LineNo == 1)
                .Select(l => l.OrderedQty).FirstAsync();
        }

        private async Task<decimal> ReadPoOrderQtyAsync(string poNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseOrderId == poNo && l.LineNo == 1)
                .Select(l => l.OrderQty).FirstAsync();
        }

        private async Task<decimal> ReadSoShippedQtyAsync(string soNo)
        {
            using var _ = NewScope(out var db);
            return await db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.SalesOrderId == soNo && l.LineNo == 1)
                .Select(l => l.ShippedQty).FirstAsync();
        }

        private async Task<bool> SoExistsAsync(string soNo)
        {
            using var _ = NewScope(out var db);
            return await db.SalesOrders.IgnoreQueryFilters().AnyAsync(o => o.SalesOrderId == soNo);
        }

        private async Task<WorkOrderStatus> ReadWorkOrderStatusAsync(string woId)
        {
            using var _ = NewScope(out var db);
            return await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.WorkOrderId == woId).Select(w => w.Status).FirstAsync();
        }

        private async Task<string?> ReadReceiptOpoIdAsync(string receiptId)
        {
            using var _ = NewScope(out var db);
            return await db.OutsourceReceipts.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OutsourceReceiptId == receiptId)
                .Select(r => r.OutsourcePurchaseOrderId).FirstAsync();
        }
    }
}
