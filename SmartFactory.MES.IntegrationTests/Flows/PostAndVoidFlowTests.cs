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
    /// 作廢貫穿：進貨 → 入庫過帳 → 應付認列 → 作廢發票 → 作廢過帳。
    ///
    /// 作廢是最容易只做一半的動作（改個狀態就收工），而它要撤銷的是三份帳：
    /// 庫存在手量、應付子帳、總帳。本測試要求全部回到原點：
    ///   - 在手量歸零、PO 累計進貨量回補
    ///   - 應付未清餘額歸零
    ///   - 總帳每一個科目的淨額都是 0（不是只看合計，那會互相掩蓋）
    /// </summary>
    [Collection("Integration")]
    public class PostAndVoidFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZVOID";

        private const string Mat = "FLOW-VOID-MAT";
        private const string Po  = "FLOW-VOID-PO";

        private static readonly DateOnly RecvDate = new(2097, 5, 10);

        public PostAndVoidFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "作廢流程測試料")).GetAwaiter().GetResult();

        [Fact]
        public async Task PostThenVoid_UnwindsInventoryApAndGlToZero()
        {
            const decimal qty     = 20m;
            const decimal price   = 500m;
            const decimal taxRate = 0.05m;
            const decimal net     = qty * price;    // 10,000
            const decimal gross   = net * (1 + taxRate);

            // ── ① 採購單 → ② 進貨單 ────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var po = new PurchaseOrder
                {
                    PurchaseOrderId = Po, OrderDate = RecvDate, CreatedBy = UserId,
                    SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                po.Lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                    OrderQty = qty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            string grId;
            using (var scope = NewScope(out var db))
            {
                var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = qty, StockQty = qty, UomCode = Uom, StockUomCode = Uom,
                            UnitPrice = price, TaxRate = taxRate, LineAmount = net, TotalAmount = gross,
                        }
                    ],
                }, UserId, Plant);
                grId = gr.GoodsReceiptId;
            }

            // ── ③ 入庫過帳 → ④ 應付認列 ────────────────────────────────────
            string apId;
            using (var scope = NewScope(out var db))
            {
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
                var line = gr.Lines.Single();

                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = grId, LineNo = line.LineNo,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                apId = (await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, qty)).APInvoiceId;

                Assert.Equal(qty, await QtyOnHandAsync(db, Plant, Mat));
            }

            // ── ⑤ 作廢應付發票（回沖認列分錄）───────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IAPInvoiceService>(scope).VoidAsync(apId);
                Assert.Equal(0m, await Svc<IAPInvoiceService>(scope).GetOutstandingBalanceAsync(Supplier, Plant));
            }

            // ── ⑥ 作廢進貨過帳（沖銷庫存異動 + 回沖入庫分錄 + 回補 PO 進貨量）─
            using (var scope = NewScope(out var db))
            {
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);

                await Svc<IInventoryPostingService>(scope).VoidDocAsync(new VoidDocRequest
                {
                    PlantId = Plant, DocNo = grId, Reason = "流程測試作廢",
                    CreatedBy = UserId, SetDocVoid = () => gr.Status = DocStatus.Void,
                });
                await Svc<IPurchaseService>(scope).RestorePoReceivedQtyAsync(gr.Lines);
                await db.SaveChangesAsync();
            }

            // ── ⑦ 三份帳全部回到原點 ────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(0m, await QtyOnHandAsync(db, Plant, Mat));

                var poLine = await db.PurchaseOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.PurchaseOrderId == Po && l.LineNo == 1);
                Assert.Equal(0m, poLine.ReceivedQty);   // PO 額度不得被作廢單永久占用

                await AssertEveryEntryBalancedAsync(db, Plant);

                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.All(gl, kv => Assert.True(kv.Value == 0m,
                    $"科目 {kv.Key} 作廢後淨額應為 0，實際 {kv.Value}"));

                // 稽核：作廢單據不得再被視為有效收料／未核銷暫估
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
                Assert.Empty(await audit.AuditGrIrAsync(Plant));
                Assert.Empty(await audit.AuditPoReceivedQtyDriftAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }
    }
}
