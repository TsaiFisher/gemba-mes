using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 委外貫穿：原料入庫 → 委外發料 → 委外收貨 → 應付認列 → 付款。
    ///
    /// 委外的帳最容易兩頭錯：發料把料移到供應商廠區（所有權未移轉，仍是本公司存貨，
    /// 不得認列損失），收貨才是真正的加值（加工費）。本測試釘住：
    ///   - 在外原料減少、委外成品入庫
    ///   - 存貨科目淨額 = 原料成本 + 加工費（發料不入帳、收貨只入加工費）
    ///   - 2152 暫估應付：收貨貸、應付認列借 → 淨額 0
    ///   - 2100 應付帳款：認列貸、付款借 → 淨額 0
    ///   - §3.3 在外原料平衡稽核無異常
    ///
    /// 成本口徑不在本測試範圍：這裡的過帳是自行組裝的，成品入庫單價直接給加工費，
    /// 沒有經過收貨確認入口那段「把在外原料併進成品成本」的計算。
    /// 成品到底值多少，見 <see cref="OutsourceCostBasisFlowTests"/>。
    /// </summary>
    [Collection("Integration")]
    public class OutsourceFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZOSF";

        private const string Mat     = "FLOW-OSP-MAT";
        private const string Fg      = "FLOW-OSP-FG";
        private const string Routing = "FLOW-OSP-RT";
        private const string Batch   = "FLOW-OSP-PB";
        private const string Wo      = "FLOW-OSP-WO";
        private const string Opo     = "FLOW-OSP-OPO";
        private const string OoDoc   = "FLOW-OSP-OO";
        private const string Rcpt    = "FLOW-OSP-OR";

        private static readonly DateOnly StockDate = new(2097, 8, 1);
        private static readonly DateOnly IssueDate = new(2097, 8, 5);
        private static readonly DateOnly RecvDate  = new(2097, 8, 20);
        private static readonly DateOnly PayDate   = new(2097, 8, 28);

        public OutsourceFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "委外流程原料"), (Fg, "委外流程成品"));
            using var scope = NewScope(out var db);
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
        public async Task OutsourceIssueToPayment_KeepsInventoryValueAndGlConsistent()
        {
            const decimal matQty   = 200m;
            const decimal matCost  = 30m;
            const decimal issueQty = 100m;      // 委外發料
            const decimal recvQty  = 100m;      // 委外收回成品（BOM 1:1）
            const decimal fee      = 25m;       // 加工單價
            const decimal feeTotal = recvQty * fee;      // 2,500
            const decimal openVal  = matQty * matCost;   // 6,000

            // ── ① 原料入庫 ────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-OSP-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = matQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = matCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 委外採購單 + 工單 + 途程 BOM（1 成品耗 1 原料）─────────────
            using (var scope = NewScope(out var db))
            {
                db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Fg, PlantId = Plant });
                db.RoutingOperations.Add(new RoutingOperation
                {
                    RoutingId = Routing, OperationSeq = 10, WorkCenterId = "FLOW-OSP-WC",
                    TM_UNIT = 1m, UomCode = Uom,
                });
                db.RoutingBoms.Add(new RoutingBom
                {
                    RoutingId = Routing, OperationSeq = 10, BomSeq = 1,
                    ProductCode = Mat, Quantity = 1m, UomCode = Uom,
                });
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-OSP-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = recvQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = Routing, Quantity = recvQty,
                    Status = WorkOrderStatus.Released, PlantId = Plant,
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
                    OperationSeq = 10, ProductCode = Fg, Qty = recvQty, UomCode = Uom,
                });
                await db.SaveChangesAsync();
            }

            // ── ③ 委外發料（OO）：料出廠但仍是本公司存貨 ─────────────────────
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
                        }
                    },
                    SetDocPosted = () => oo.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                Assert.Equal(matQty - issueQty, await QtyOnHandAsync(db, Plant, Mat));
            }

            // ── ④ 委外收貨（OI，Purchase 類）＋ 應付認列 ─────────────────────
            string apId;
            using (var scope = NewScope(out var db))
            {
                var rec = new OutsourceReceipt
                {
                    OutsourceReceiptId = Rcpt, OutsourcePurchaseOrderId = Opo,
                    SupplierCode = Supplier, ReceiptDate = RecvDate, CreatedBy = UserId,
                    ExchangeRate = 1m, TypeCode = "OI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                rec.Lines.Add(new OutsourceReceiptLine
                {
                    OutsourceReceiptId = Rcpt, LineNo = 1, OpoLineNo = 1, ProductCode = Fg,
                    ReceiptQty = recvQty, StockQty = recvQty, UomCode = Uom, StockUomCode = Uom,
                    UnitCost = fee, WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceReceipts.Add(rec);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Rcpt, MovementDate = RecvDate,
                    TypeCode = "OI", TypeCategory = "Purchase", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Fg, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = recvQty, UomCode = Uom, UnitPrice = fee, SetQtyPutAway = _ => { },
                        }
                    },
                    SetDocPosted = () => rec.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                apId = (await Svc<IAPInvoiceService>(scope)
                    .GenerateFromOutsourceReceiptAsync(Rcpt)).APInvoiceId;

                Assert.Equal(recvQty, await QtyOnHandAsync(db, Plant, Fg));
            }

            // ── ⑤ 付款 ────────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var payment = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, PayDate, PaymentMethod.Wire, null, 1m, feeTotal, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, feeTotal) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(payment.APPaymentId);

                Assert.Equal(0m, await Svc<IAPInvoiceService>(scope)
                    .GetOutstandingBalanceAsync(Supplier, Plant));
            }

            // ── ⑥ 全鏈收斂 ────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 存貨 = 原料成本 + 加工費（發料不入帳，收貨只入加工費）
                Assert.Equal(openVal + feeTotal, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                // 委外發料不得被認列為損失
                Assert.Equal(0m, Net(gl, StandardAccounts.ScrapLossCode));
                // 2152 只剩期初原料入庫那一筆未沖（本流程未替期初料開 AP）；
                // 委外加工費的部分（收貨貸 2,500、認列借 2,500）已完全收斂。
                Assert.Equal(-openVal, Net(gl, StandardAccounts.GrIrAPClearingCode));
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(0m, gl.Values.Sum());

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditOutsourceBalanceAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }
    }
}
