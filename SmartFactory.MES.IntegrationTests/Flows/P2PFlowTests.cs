using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// P2P 貫穿：採購單 → 進貨單 → 入庫過帳 → 應付認列 → 付款。
    ///
    /// 這條鏈的每一段先前都各自有測試，但沒有任何一支從頭跑到尾再回頭問
    /// 「庫存、應付子帳、總帳三者對不對得起來」。本測試就是釘這件事：
    ///   - 2152 進貨暫估應付：入庫時貸記、認列時借記 → 全鏈結束必須淨額為 0
    ///   - 2100 應付帳款：認列時貸記、付款時借記 → 全鏈結束必須淨額為 0
    ///   - 1401 存貨：只增不減（未出庫），淨額 = 進貨成本
    ///   - 每張傳票各自借貸平衡，且 §3.1／§3.2 稽核無異常
    /// </summary>
    [Collection("Integration")]
    public class P2PFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZP2P";

        private const string Mat = "FLOW-MAT";
        private const string Po  = "FLOW-PO-1";

        private static readonly DateOnly OrderDate = new(2097, 3, 1);
        private static readonly DateOnly RecvDate  = new(2097, 3, 10);
        private static readonly DateOnly PayDate   = new(2097, 3, 20);

        public P2PFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "流程測試原料")).GetAwaiter().GetResult();

        /// <summary>
        /// 一次跑完整條鏈再回頭驗帳。刻意寫成單一測試方法：每一步都以前一步的
        /// 單據狀態為前提，拆成多個 [Fact] 只會得到隨機紅燈。
        /// </summary>
        [Fact]
        public async Task PurchaseToPayment_FullChain_KeepsInventoryApAndGlConsistent()
        {
            const decimal orderQty = 100m;
            const decimal price    = 250m;
            const decimal taxRate  = 0.05m;
            const decimal net      = orderQty * price;          // 25,000
            const decimal tax      = net * taxRate;             //  1,250
            const decimal gross    = net + tax;                 // 26,250

            // ── ① 採購單（已核准）──────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var po = new PurchaseOrder
                {
                    PurchaseOrderId = Po, OrderDate = OrderDate, CreatedBy = UserId,
                    SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                po.Lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                    OrderQty = orderQty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            // ── ② 進貨單（由採購單拋轉，會回寫 PO 累計進貨量）────────────────
            string grId;
            using (var scope = NewScope(out var db))
            {
                var purchase = Svc<IPurchaseService>(scope);
                var gr = await purchase.CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = orderQty, StockQty = orderQty,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                            TaxRate = taxRate, LineAmount = net, TotalAmount = gross,
                        }
                    ],
                }, UserId, Plant);
                grId = gr.GoodsReceiptId;

                var poLine = await db.PurchaseOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.PurchaseOrderId == Po && l.LineNo == 1);
                Assert.Equal(orderQty, poLine.ReceivedQty);   // 前後單數量拋轉
            }

            // ── ③ 入庫過帳（借 存貨 / 貸 進貨暫估應付）──────────────────────
            using (var scope = NewScope(out var db))
            {
                var posting = Svc<IInventoryPostingService>(scope);
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
                var line = gr.Lines.Single();

                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = grId, LineNo = line.LineNo,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = line.StockQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                Assert.Equal(orderQty, await QtyOnHandAsync(db, Plant, Mat));
            }

            // ── ④ 應付認列（借 進貨暫估應付 + 進項稅 / 貸 應付帳款）─────────
            string apId;
            using (var scope = NewScope(out var db))
            {
                var ap = Svc<IAPInvoiceService>(scope);
                var invoice = await ap.AccrueForReceivedQtyAsync(grId, 1, orderQty);
                apId = invoice.APInvoiceId;

                Assert.Equal(gross, invoice.TotalAmount);
                Assert.Equal(gross, await ap.GetOutstandingBalanceAsync(Supplier, Plant));
            }

            // ── ⑤ 付款（借 應付帳款 / 貸 銀行存款）──────────────────────────
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var payment = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, PayDate, PaymentMethod.Wire, null, 1m, gross, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, gross) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(payment.APPaymentId);

                var invoice = await db.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.Paid, invoice.Status);
                Assert.Equal(gross, invoice.PaidAmount);
                Assert.Equal(0m, await Svc<IAPInvoiceService>(scope).GetOutstandingBalanceAsync(Supplier, Plant));
            }

            // ── ⑥ 全鏈收斂驗證 ─────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);

                var gl = await GlNetByAccountAsync(db, Plant);

                // 2152 暫估應付：入庫貸 25,000、認列借 25,000 → 收斂為 0
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
                // 2100 應付帳款：認列貸 26,250、付款借 26,250 → 收斂為 0
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsPayableCode));
                // 1401 存貨：僅入庫，淨額＝進貨成本（未稅）
                Assert.Equal(net, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                // 1155 進項稅額：認列時借記，未再沖銷
                Assert.Equal(tax, Net(gl, StandardAccounts.InputVatCode));
                // 全廠總帳借貸相等
                Assert.Equal(0m, gl.Values.Sum());

                // §3.1 採購超收 / §3.2 GR-IR：整條鏈跑完不得有任何異常
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
                Assert.Empty(await audit.AuditGrIrAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditPoReceivedQtyDriftAsync(Plant));
            }
        }
    }
}
