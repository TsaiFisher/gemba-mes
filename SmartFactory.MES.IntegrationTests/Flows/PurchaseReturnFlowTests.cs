using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
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
    /// 進貨退出貫穿：進貨 → 入庫 → 應付認列 → 部分退貨出庫 → 應付回沖。
    ///
    /// 退貨是最容易在總帳留下假損失的一段：退料出庫若被當成一般出庫，
    /// 會借記報廢損失而不是沖回進貨暫估應付，帳面憑空多一筆費用。
    /// 本測試釘住退貨後的三方一致：
    ///   - 在手量 = 進貨量 − 退貨量
    ///   - 存貨科目淨額 = 留存數量 × 成本
    ///   - 2152 暫估應付：入庫貸、認列借、退貨借、AP 回沖貸 → 淨額為 0
    ///   - 應付未清餘額 = 未退部分的含稅金額
    /// </summary>
    [Collection("Integration")]
    public class PurchaseReturnFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZRTN";

        private const string Mat = "FLOW-RTN-MAT";
        private const string Po  = "FLOW-RTN-PO";
        private const string Grt = "FLOW-RTN-GRT";

        private static readonly DateOnly RecvDate = new(2097, 7, 1);
        private static readonly DateOnly RtnDate  = new(2097, 7, 15);

        public PurchaseReturnFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "退貨流程測試料"));
            using var scope = NewScope(out var db);
            await db.GoodsReturns.IgnoreQueryFilters().Where(g => g.PlantId == Plant).ExecuteDeleteAsync();
        }

        [Fact]
        public async Task PurchaseReturn_ReversesInventoryApAndGrIrWithoutFakeLoss()
        {
            const decimal recvQty  = 100m;
            const decimal price    = 200m;
            const decimal taxRate  = 0.05m;
            const decimal rtnQty   = 30m;
            const decimal keptQty  = recvQty - rtnQty;                 // 70
            const decimal net      = recvQty * price;                  // 20,000
            const decimal gross    = net * (1 + taxRate);              // 21,000
            const decimal keptGross = keptQty * price * (1 + taxRate); // 14,700

            // ── ① 採購 → ② 進貨 → ③ 入庫 → ④ 應付認列 ─────────────────────
            string grId;
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
                    OrderQty = recvQty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();

                var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = recvQty, StockQty = recvQty, UomCode = Uom, StockUomCode = Uom,
                            UnitPrice = price, TaxRate = taxRate, LineAmount = net, TotalAmount = gross,
                        }
                    ],
                }, UserId, Plant);
                grId = gr.GoodsReceiptId;
            }

            using (var scope = NewScope(out var db))
            {
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
                var line = gr.Lines.Single();

                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = grId, LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = recvQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, recvQty);
            }

            // ── ⑤ 退貨出庫（PF 進貨退出）＋ 應付回沖 ────────────────────────
            using (var scope = NewScope(out var db))
            {
                var grt = new GoodsReturn
                {
                    GoodsReturnId = Grt, ReturnDate = RtnDate, CreatedBy = UserId,
                    SupplierCode = Supplier, TypeCode = "PF", TypeCategory = "Purchase",
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                db.GoodsReturns.Add(grt);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Grt, MovementDate = RtnDate,
                    TypeCode = "PF", TypeCategory = "Purchase", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Mat, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = rtnQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        }
                    },
                    SetDocPosted = () => grt.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                await Svc<IAPInvoiceService>(scope).ReverseForReturnedQtyAsync(grId, 1, rtnQty);
            }

            // ── ⑥ 三方一致 ─────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(keptQty, await QtyOnHandAsync(db, Plant, Mat));

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 存貨：留存數量 × 成本
                Assert.Equal(keptQty * price, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                // 暫估應付：入庫貸 20,000、認列借 20,000、退貨借 6,000、AP 回沖貸 6,000 → 0
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
                // 退貨不得憑空產生報廢損失
                Assert.Equal(0m, Net(gl, StandardAccounts.ScrapLossCode));
                // 應付帳款：認列 21,000 − 回沖 6,300
                Assert.Equal(-keptGross, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(0m, gl.Values.Sum());

                Assert.Equal(keptGross,
                    await Svc<IAPInvoiceService>(scope).GetOutstandingBalanceAsync(Supplier, Plant));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
            }
        }
    }
}
