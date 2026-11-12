using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §5.1 的 2-11：委外**超耗扣款**與 AP 金額連動。
    ///
    /// 既有的 <c>PUR/OutsourceScrapPenaltyTests</c> 驗的是扣款金額本身（DB 組裝 → 逐料 AVCO），
    /// 到 <c>ExcessScrapPenalty</c> 寫回為止就結束了。本測試接著問下一個問題：
    ///
    ///   **扣款之後，帳平不平。**
    ///
    /// 扣款是應付發票的負項（§1.5／§4.4：直接扣減應付加工費，不另開折讓單），
    /// 於是「應付」比「收貨時掛的暫估」少了一個扣款金額。2152 是清算科目——
    /// 一張委外單走完全程之後它必須回到 0；回不到 0 就表示有一筆錢沒有落點，
    /// 而且它不會讓任何一張傳票失衡，月結時只會看到一個沖不掉的餘額。
    /// 這與 Wave 1 的暫估尾差是同一個形狀，差別只在這裡不是尾數而是整筆扣款。
    ///
    /// 場景（BOM 1:1，允收損耗 5%）：
    ///   發料 100 @ 30 → 收回 90 個成品 @ 加工費 25
    ///   預期耗用 90、實際發料 100 → 損耗 10、允收 5 → 超耗 5 → 扣款 5 × 30 = 150
    ///   應付 = 90 × 25 − 150 = 2,100，而收貨時掛的暫估 = 90 × 25 = 2,250
    /// </summary>
    [Collection("Integration")]
    public class OutsourceScrapPenaltyFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZOSP";

        // 單號前綴刻意與 OutsourceFlowTests 的 FLOW-OSP-* 區隔：那一支跑在 ZZOSF 廠，
        // 但這裡的清場是以「單號」為鍵刪 OPO 明細（明細沒有廠別欄位），撞號會清掉別人的資料。
        private const string Mat     = "FLOW-OSS-MAT";
        private const string Fg      = "FLOW-OSS-FG";
        private const string Batch   = "FLOW-OSS-PB";
        private const string Routing = "FLOW-OSS-RT";
        private const string Wo      = "FLOW-OSS-WO";
        private const string Opo     = "FLOW-OSS-OPO";
        private const string OoDoc   = "FLOW-OSS-OO";
        private const string Rcpt    = "FLOW-OSS-OR";

        private static readonly DateOnly StockDate = new(2097, 12, 1);
        private static readonly DateOnly IssueDate = new(2097, 12, 5);
        private static readonly DateOnly RecvDate  = new(2097, 12, 20);

        public OutsourceScrapPenaltyFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "委外超耗原料"), (Fg, "委外超耗成品"));
            using var scope = NewScope(out var db);
            await db.APInvoices.IgnoreQueryFilters().Where(i => i.PlantId == Plant).ExecuteDeleteAsync();
            await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.PlantId == Plant).ExecuteDeleteAsync();
            await db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.OutsourcePurchaseOrderId == Opo).ExecuteDeleteAsync();
            await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
            await db.RoutingBoms.IgnoreQueryFilters().Where(b => b.RoutingId == Routing).ExecuteDeleteAsync();
            await db.RoutingOperations.IgnoreQueryFilters().Where(o => o.RoutingId == Routing).ExecuteDeleteAsync();
            await db.Routings.IgnoreQueryFilters().Where(r => r.RoutingId == Routing).ExecuteDeleteAsync();
        }

        [Fact]
        public async Task OutsourceScrapPenalty_ReducesPayableAndStillClearsGrIrToZero()
        {
            const decimal matQty = 200m, matCost = 30m;
            const decimal issueQty = 100m, recvQty = 90m;      // BOM 1:1 → 預期耗用 90
            const decimal fee = 25m, allowedScrapRate = 0.05m;

            const decimal expectedPenalty = 150m;              // 超耗 5 個 × 30
            const decimal accrued  = recvQty * fee;            // 2,250 收貨時掛的暫估
            const decimal payable  = accrued - expectedPenalty; // 2,100 實際應付

            // ── ① 原料入庫 ────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-OSS-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = matQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = matCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 委外工單與委外採購單（途程 BOM 1:1）─────────────────────────
            using (var scope = NewScope(out var db))
            {
                db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Fg, PlantId = Plant });
                db.RoutingOperations.Add(new RoutingOperation
                {
                    RoutingId = Routing, OperationSeq = 10, WorkCenterId = "FLOW-OSS-WC",
                    TM_UNIT = 1m, UomCode = Uom,
                });
                db.RoutingBoms.Add(new RoutingBom
                {
                    RoutingId = Routing, OperationSeq = 10, BomSeq = 1,
                    ProductCode = Mat, Quantity = 1m, UomCode = Uom,
                });
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-OSS-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = issueQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = Routing, Quantity = issueQty,
                    Status = WorkOrderStatus.Released, PlantId = Plant,
                    IsOutsource = true, OutsourceSupplierCode = Supplier,
                });
                db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
                {
                    OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier,
                    OrderDate = IssueDate, Status = OutsourcePoStatus.Confirmed,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
                {
                    OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = Wo,
                    OperationSeq = 10, ProductCode = Fg, Qty = issueQty, UomCode = Uom,
                });
                await db.SaveChangesAsync();
            }

            // ── ③ 委外發料 100（總帳不動，料的價值留在存貨科目）─────────────────
            using (var scope = NewScope(out var db))
            {
                var oo = new OutsourceMaterialDoc
                {
                    OutsourceMaterialDocId = OoDoc, IssueDate = IssueDate,
                    TypeCode = "OO", TypeCategory = "Outsource", SupplierCode = Supplier,
                    WorkOrderCode = Wo, PlantId = Plant, CreatedByRoleId = RoleId,
                };
                db.OutsourceMaterialDocs.Add(oo);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = OoDoc, MovementDate = IssueDate,
                    TypeCode = "OO", TypeCategory = "Outsource", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Mat, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = issueQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        },
                    },
                    SetDocPosted = () => oo.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }

            // ── ④ 委外收貨 90（少 10 個＝損耗，其中 5 個超出允收）────────────────
            using (var scope = NewScope(out var db))
            {
                var rec = new OutsourceReceipt
                {
                    OutsourceReceiptId = Rcpt, OutsourcePurchaseOrderId = Opo,
                    SupplierCode = Supplier, ReceiptDate = RecvDate, CreatedBy = UserId,
                    ExchangeRate = 1m, TypeCode = "OI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                    // 短交結案：訂購 100 只收回 90，且不會再有下一批。收貨量沒到訂購量時
                    // 系統分不出「還在廠商那裡加工」與「已經報廢」，所以要結算超耗
                    // 得有人說一聲；不勾的話正確答案是 0（見 OutsourceSplitReceiptFlowTests）。
                    IsFinalReceipt = true,
                };
                rec.Lines.Add(new OutsourceReceiptLine
                {
                    OutsourceReceiptId = Rcpt, LineNo = 1, OpoLineNo = 1, ProductCode = Fg,
                    ReceiptQty = recvQty, StockQty = recvQty, UomCode = Uom, StockUomCode = Uom,
                    UnitCost = fee, AllowedScrapRate = allowedScrapRate,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceReceipts.Add(rec);
                await db.SaveChangesAsync();
            }

            await ConfirmReceiptAsync();

            // ── ⑤ 扣款算對，且確實反映到應付發票 ─────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var line = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.OutsourceReceiptId == Rcpt);
                Assert.Equal(expectedPenalty, line.ExcessScrapPenalty);

                var invoice = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Include(i => i.Lines)
                    .SingleAsync(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == Rcpt);

                // 扣款是發票負項，不另開折讓單
                Assert.Equal(-expectedPenalty, Assert.Single(invoice.Lines.Where(l => l.LineAmount < 0)).LineAmount);
                Assert.Equal(payable, invoice.TotalAmount);
            }

            // ── ⑥ 走完全程後，清算科目必須歸零 ───────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 應付掛的是扣款後的淨額
                Assert.Equal(-payable, Net(gl, StandardAccounts.AccountsPayableCode));

                // 2152 只看**這條委外鏈自己的兩張傳票**（收貨過帳 + 應付認列）：
                // 步驟 ① 的期初原料進貨也會貸記 2152，而本測試沒有替它認列應付，
                // 那 6,000 是佈景不是待驗事實，混進全廠淨額只會蓋住真正要看的 150。
                var invoiceId = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == Rcpt)
                    .Select(i => i.APInvoiceId).SingleAsync();

                var grIrOnChain = await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode
                             && (l.JournalEntry!.SourceDocId == Rcpt
                              || l.JournalEntry.SourceDocId == invoiceId))
                    .SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0m;

                // 收貨貸記暫估 2,250、認列借記 2,100 → 差額 150 必須有落點，
                // 不得留在 2152 當作永遠沖不掉的餘額。
                Assert.Equal(0m, grIrOnChain);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditOutsourceBalanceAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }

        /// <summary>
        /// 走真正的收貨確認入口：罰扣、庫存過帳、在途轉換、自動開 AP 是同一個交易內的四件事，
        /// 分開驅動就驗不到它們之間的金額銜接。簡化模式是該入口直接過帳庫存的前提，
        /// 它是全域參數，故用完必須清回未設定。
        /// </summary>
        private async Task ConfirmReceiptAsync()
        {
            using (var scope = NewScope(out _))
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "true");
            try
            {
                using var scope = NewScope(out var db);
                var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
                var ctrl = new OutsourceReceiptController(
                    db,
                    sp.GetRequiredService<IUserContext>(),
                    sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IDateTimeService>(),
                    sp.GetRequiredService<IApprovalService>(),
                    sp.GetRequiredService<IDocNumberService>(),
                    sp.GetRequiredService<IInventoryPostingService>(),
                    sp.GetRequiredService<IPlantParamService>(),
                    sp.GetRequiredService<IUomService>(),
                    sp.GetRequiredService<ICurrencyService>(),
                    sp.GetRequiredService<IAPInvoiceService>(),
                    sp.GetRequiredService<IExportService>(),
                    sp.GetRequiredService<IInventoryReservationService>(),
                    sp.GetRequiredService<IOutsourceReceiptSettlementService>(),
                    sp.GetRequiredService<IJournalEntryService>(),
                    sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext { RequestServices = sp },
                    },
                };

                var json = Assert.IsType<JsonResult>(await ctrl.Confirm(Rcpt));
                var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
                var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value);
                Assert.True(Equals(ok, true), $"委外收貨確認應成功：{msg}");
            }
            finally
            {
                using var scope = NewScope(out _);
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, null);
            }
        }
    }
}
