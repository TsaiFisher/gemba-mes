using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §5.1 的 2-11 續篇：**分批收貨**下的委外超耗扣款與在外原料歸屬。
    ///
    /// <c>OutsourceScrapPenaltyFlowTests</c> 只跑了「一次收足」，而
    /// <c>OutsourceScrapPenaltyService</c> 的註解自己寫著：
    ///
    ///   「邊界：以『單筆收貨量 vs 該工單全部發料量』為粒度，分批收貨的跨批彙總對帳屬另案。」
    ///
    /// 「另案」在這裡不是延後實作，是**錯的答案**：每一批都拿自己的收貨量去和
    /// 整張工單的發料量比，於是「還沒收回來的部分」被當成已經報廢掉了，而下一批
    /// 收回來時又重算一次。批數越多罰得越兇，且永遠不會回頭修正。
    ///
    /// 兩支測試分別釘住兩個後果：
    ///   ① 完全沒有損耗的委外單，分兩批收就被罰到超過整筆加工費；
    ///   ② 真的有超耗時，扣款金額錯得離譜，而且被報廢掉的料錢
    ///      永遠留在存貨科目裡（庫存明細帳合計 &lt; 總帳存貨）。
    ///
    /// ②的第二半是「在外原料」缺口：委外發料屬 Outsource 類，庫存數量出而總帳不動，
    /// 料的價值一直留在 1401；收貨時併回成品的只有**理論消耗**（收貨量 × BOM），
    /// 被加工掉的差額就沒有任何落點。它不會讓任何一張傳票失衡，
    /// <c>AuditOutsourceBalanceAsync</c> 又只驗數量、只抓負餘額，所以完全沉默。
    /// </summary>
    [Collection("Integration")]
    public class OutsourceSplitReceiptFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZOSB";

        // 單號前綴與 ZZOSP／ZZOSF 兩支刻意錯開：OPO 明細沒有廠別欄位，
        // 清場以單號為鍵，撞號會清掉別人的資料（見 OutsourceScrapPenaltyFlowTests 同段註解）。
        private const string Mat     = "FLOW-OSB-MAT";
        private const string Fg      = "FLOW-OSB-FG";
        private const string Batch   = "FLOW-OSB-PB";
        private const string Routing = "FLOW-OSB-RT";
        private const string Wo      = "FLOW-OSB-WO";
        private const string Opo     = "FLOW-OSB-OPO";
        private const string OoDoc   = "FLOW-OSB-OO";
        private const string Rcpt1   = "FLOW-OSB-OR1";
        private const string Rcpt2   = "FLOW-OSB-OR2";

        private static readonly DateOnly StockDate = new(2098, 3, 1);
        private static readonly DateOnly IssueDate = new(2098, 3, 5);
        private static readonly DateOnly Recv1Date = new(2098, 3, 15);
        private static readonly DateOnly Recv2Date = new(2098, 3, 25);

        private const decimal MatOpenQty = 200m, MatCost = 30m;
        private const decimal Fee = 25m, AllowedScrapRate = 0.05m;

        public OutsourceSplitReceiptFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "委外分批原料"), (Fg, "委外分批成品"));
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

        /// <summary>
        /// 一顆料都沒報廢的委外單，分兩批收回。收足＝訂購量，實際損耗率 0%，
        /// 合約允收 5%——扣款的正確答案是 0，而且兩批都是 0。
        ///
        /// 現行實作在第一批就把「還在廠商那裡的另外 50 個」算成報廢：
        /// 損耗率 (110−50)/110、扣掉允收 5% 之後乘上**全部發料量**，
        /// 第二批再算一次。兩批合計會超過整張單的加工費——廠商做完了還要倒貼。
        /// </summary>
        [Fact]
        public async Task SplitReceipts_WithNoScrapAtAll_MustChargeNothing()
        {
            const decimal issueQty = 100m, orderQty = 100m;
            const decimal recv1 = 50m, recv2 = 50m;

            await SeedChainAsync(issueQty: issueQty, orderQty: orderQty, bomPerUnit: 1m);
            await ReceiveAsync(Rcpt1, Recv1Date, recv1);
            await ReceiveAsync(Rcpt2, Recv2Date, recv2);

            using var scope = NewScope(out var db);

            var lines = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OutsourceReceiptId == Rcpt1 || l.OutsourceReceiptId == Rcpt2)
                .OrderBy(l => l.OutsourceReceiptId).ToListAsync();
            Assert.Equal(2, lines.Count);

            // 收足且零損耗 → 兩批都不該有扣款
            Assert.Equal(0m, lines[0].ExcessScrapPenalty);
            Assert.Equal(0m, lines[1].ExcessScrapPenalty);

            // 應付＝純加工費，沒有任何負項
            var payable = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "OutsourceReceipt"
                         && (i.SourceDocId == Rcpt1 || i.SourceDocId == Rcpt2))
                .SumAsync(i => i.TotalAmount);
            Assert.Equal((recv1 + recv2) * Fee, payable);

            await AssertChainClosesCleanAsync(db, issueQty * MatCost, expectedPenalty: 0m);
        }

        /// <summary>
        /// 實際會發生的情況：預期會有損耗，故多發了料（發 110 做 100）。
        /// 廠商分兩批交足 100 個，實際耗掉 110、理論耗用 100 → 損耗 10 個。
        /// 允收 5% × 110 = 5.5 個，超耗 4.5 個 → 扣款 4.5 × 30 = 135，**一次算清**。
        ///
        /// 同時釘住在外原料的落點：發出去的 3,300 元料，除了廠商賠的 135 元之外
        /// 全部必須回到成品身上（含允收範圍內的正常損耗——那本來就是產品成本），
        /// 於是「總帳存貨科目淨額」必須等於「庫存明細帳合計」。
        /// 現行實作只併回理論消耗，差額 300 元永久留在 1401，沒有任何稽核看得到。
        /// </summary>
        [Fact]
        public async Task SplitReceipts_ExcessScrap_IsSettledOnceAndMaterialValueLandsSomewhere()
        {
            const decimal issueQty = 110m, orderQty = 100m;
            const decimal recv1 = 60m, recv2 = 40m;
            const decimal expectedPenalty = 135m;   // 超耗 4.5 個 × 30

            await SeedChainAsync(issueQty: issueQty, orderQty: orderQty, bomPerUnit: 1m);
            await ReceiveAsync(Rcpt1, Recv1Date, recv1);
            await ReceiveAsync(Rcpt2, Recv2Date, recv2);

            using var scope = NewScope(out var db);

            var l1 = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.OutsourceReceiptId == Rcpt1);
            var l2 = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.OutsourceReceiptId == Rcpt2);

            // 中間批不得預扣：那時「還沒收回來的量」尚未變成損耗
            Assert.Equal(0m, l1.ExcessScrapPenalty);
            Assert.Equal(expectedPenalty, l2.ExcessScrapPenalty);

            var payable = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "OutsourceReceipt"
                         && (i.SourceDocId == Rcpt1 || i.SourceDocId == Rcpt2))
                .SumAsync(i => i.TotalAmount);
            Assert.Equal((recv1 + recv2) * Fee - expectedPenalty, payable);

            await AssertChainClosesCleanAsync(db, issueQty * MatCost, expectedPenalty);
        }

        /// <summary>
        /// 走完全程後必須同時成立的三件事：
        ///   ① 總帳存貨科目淨額 == 庫存明細帳（Σ QtyOnHand × UnitCost）——在外原料有落點；
        ///   ② 這條委外鏈自己的 2152 歸零——暫估與應付對得起來；
        ///   ③ 廠商賠的料錢貸在存貨（1401）而不是進料差異（5151）——
        ///      那批料是實體消失，不是買貴買便宜。
        /// </summary>
        private async Task AssertChainClosesCleanAsync(
            SmartFactory.MES.Data.ApplicationDbContext db, decimal issuedValue, decimal expectedPenalty)
        {
            await AssertEveryEntryBalancedAsync(db, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);

            var ledger = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant)
                .SumAsync(i => (decimal?)(i.QtyOnHand * i.UnitCost)) ?? 0m;

            // ① 在外原料的落點：總帳與明細帳必須一致
            Assert.Equal(
                Math.Round(ledger, 2),
                Math.Round(Net(gl, StandardAccounts.RawMaterialInventoryCode), 2));

            // ③ 扣款不得被記成進料差異
            Assert.Equal(0m, Net(gl, StandardAccounts.LandedCostVarianceCode));

            // ② 2152 只看這條鏈自己的傳票：期初原料進貨也貸記 2152，
            //    但本測試沒有替它認列應付，那是佈景不是待驗事實。
            var invoiceIds = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "OutsourceReceipt"
                         && (i.SourceDocId == Rcpt1 || i.SourceDocId == Rcpt2))
                .Select(i => i.APInvoiceId).ToListAsync();
            var chainDocs = invoiceIds.Concat(new[] { Rcpt1, Rcpt2 }).ToList();

            var grIr = await db.JournalLines.IgnoreQueryFilters()
                .Where(l => l.AccountCode == StandardAccounts.GrIrAPClearingCode
                         && chainDocs.Contains(l.JournalEntry!.SourceDocId!))
                .SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0m;
            Assert.Equal(0m, grIr);

            // 發出去的料價值 = 成品吸收的 + 廠商賠的
            var fgValue = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == Fg)
                .SumAsync(i => (decimal?)(i.QtyOnHand * i.UnitCost)) ?? 0m;
            var feeTotal = await db.OutsourceReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OutsourceReceiptId == Rcpt1 || l.OutsourceReceiptId == Rcpt2)
                .SumAsync(l => l.ReceiptQty * l.UnitCost);
            Assert.Equal(
                Math.Round(issuedValue - expectedPenalty + feeTotal, 2),
                Math.Round(fgValue, 2));

            using var scope = NewScope(out _);
            var audit = Svc<IDataAuditService>(scope);
            Assert.Empty(await audit.AuditOutsourceBalanceAsync(Plant));
            Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedChainAsync(decimal issueQty, decimal orderQty, decimal bomPerUnit)
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-OSB-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = MatOpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = MatCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Fg, PlantId = Plant });
                db.RoutingOperations.Add(new RoutingOperation
                {
                    RoutingId = Routing, OperationSeq = 10, WorkCenterId = "FLOW-OSB-WC",
                    TM_UNIT = 1m, UomCode = Uom,
                });
                db.RoutingBoms.Add(new RoutingBom
                {
                    RoutingId = Routing, OperationSeq = 10, BomSeq = 1,
                    ProductCode = Mat, Quantity = bomPerUnit, UomCode = Uom,
                });
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-OSB-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = orderQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = Routing, Quantity = orderQty,
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
                    OperationSeq = 10, ProductCode = Fg, Qty = orderQty, UomCode = Uom,
                });
                await db.SaveChangesAsync();
            }

            // 委外發料：庫存數量出、總帳不動，料的價值留在 1401
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
        }

        private async Task ReceiveAsync(string receiptId, DateOnly date, decimal qty)
        {
            using (var scope = NewScope(out var db))
            {
                var rec = new OutsourceReceipt
                {
                    OutsourceReceiptId = receiptId, OutsourcePurchaseOrderId = Opo,
                    SupplierCode = Supplier, ReceiptDate = date, CreatedBy = UserId,
                    ExchangeRate = 1m, TypeCode = "OI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                rec.Lines.Add(new OutsourceReceiptLine
                {
                    OutsourceReceiptId = receiptId, LineNo = 1, OpoLineNo = 1, ProductCode = Fg,
                    ReceiptQty = qty, StockQty = qty, UomCode = Uom, StockUomCode = Uom,
                    UnitCost = Fee, AllowedScrapRate = AllowedScrapRate,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceReceipts.Add(rec);
                await db.SaveChangesAsync();
            }
            await ConfirmReceiptAsync(receiptId);
        }

        /// <summary>
        /// 走真正的收貨確認入口：罰扣、庫存過帳、在途轉換、自動開 AP 是同一個交易內的四件事。
        /// 簡化模式是該入口直接過帳庫存的前提，且它是全域參數，用完必須清回未設定。
        /// </summary>
        private async Task ConfirmReceiptAsync(string receiptId)
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

                var json = Assert.IsType<JsonResult>(await ctrl.Confirm(receiptId));
                var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
                var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value);
                Assert.True(Equals(ok, true), $"委外收貨確認應成功（{receiptId}）：{msg}");
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
