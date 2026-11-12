using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// O2C 貫穿：期初入庫 → 銷貨訂單 → 出貨過帳 → 應收開立 → 收款。
    ///
    /// 釘的是出貨端的三方一致：
    ///   - 庫存：出貨後在手量 = 期初 − 出貨量
    ///   - 4100 銷貨收入 / 5100 銷貨成本：出貨產生成本、開票產生收入（毛利可驗）
    ///   - 1200 應收帳款：開票借記、收款貸記 → 全鏈結束淨額為 0
    ///   - §1.2-1 銷售超出貨 與 §1.2-2 出貨未開票 稽核無異常
    /// </summary>
    [Collection("Integration")]
    public class O2CFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZO2C";

        private const string Fg  = "FLOW-FG";
        private const string So  = "FLOW-SO-1";
        private const string Gs  = "FLOW-GS-1";

        private static readonly DateOnly StockDate = new(2097, 4, 1);
        private static readonly DateOnly ShipDate  = new(2097, 4, 10);
        private static readonly DateOnly RecvDate  = new(2097, 4, 20);

        public O2CFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Fg, "流程測試成品")).GetAwaiter().GetResult();

        [Fact]
        public async Task OrderToCash_FullChain_KeepsInventoryArAndGlConsistent()
        {
            const decimal openingQty  = 100m;
            const decimal openingCost = 120m;      // 期初單位成本
            const decimal shipQty     = 40m;
            const decimal price       = 300m;
            const decimal taxRate     = 0.05m;
            const decimal cogs        = shipQty * openingCost;   //  4,800
            const decimal revenue     = shipQty * price;         // 12,000
            const decimal tax         = revenue * taxRate;       //    600
            const decimal gross       = revenue + tax;           // 12,600

            // ── ① 期初入庫（建立成本基礎）──────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var posting = Svc<IInventoryPostingService>(scope);
                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-OPEN-1", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "",
                    Qty = openingQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = openingCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
                Assert.Equal(openingQty, await QtyOnHandAsync(db, Plant, Fg));
            }

            // ── ② 銷貨訂單（已核准）────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var so = new SalesOrder
                {
                    SalesOrderId = So, OrderDate = ShipDate, CompanyCode = Customer,
                    CreatedBy = UserId, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                so.Lines.Add(new SalesOrderLine
                {
                    SalesOrderId = So, LineNo = 1, ProductCode = Fg,
                    OrderQty = shipQty, UomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                });
                db.SalesOrders.Add(so);
                await db.SaveChangesAsync();
            }

            // ── ③ 出貨單 + 出貨過帳（借 銷貨成本 / 貸 存貨）─────────────────
            using (var scope = NewScope(out var db))
            {
                var ship = new GoodsShipment
                {
                    GoodsShipmentId = Gs, ShipDate = ShipDate, TypeCode = "GS",
                    CompanyCode = Customer, SalesOrderId = So, CreatedBy = UserId,
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                ship.Lines.Add(new GoodsShipmentLine
                {
                    GoodsShipmentId = Gs, LineNo = 1, SalesOrderId = So, SoLineNo = 1,
                    ProductCode = Fg, ShipQty = shipQty, StockQty = shipQty,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = price, TaxRate = taxRate,
                    LineAmount = revenue, TaxAmount = tax, TotalAmount = gross,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.GoodsShipments.Add(ship);

                var soLine = await db.SalesOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.SalesOrderId == So && l.LineNo == 1);
                soLine.ShippedQty += shipQty;     // 出貨回寫（Controller 同語意）
                await db.SaveChangesAsync();

                var posting = Svc<IInventoryPostingService>(scope);
                await posting.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Gs, MovementDate = ShipDate,
                    TypeCode = "GS", TypeCategory = "Sales", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Fg, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = shipQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        }
                    },
                    SetDocPosted = () => ship.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                Assert.Equal(openingQty - shipQty, await QtyOnHandAsync(db, Plant, Fg));
            }

            // ── ④ 應收發票（借 應收帳款 / 貸 銷貨收入 + 銷項稅）─────────────
            string arId;
            using (var scope = NewScope(out var db))
            {
                var ar = Svc<IARInvoiceService>(scope);
                var invoice = await ar.GenerateFromShipmentAsync(Gs);
                arId = invoice.ARInvoiceId;

                Assert.Equal(gross, invoice.TotalAmount);
                Assert.Equal(gross, await ar.GetOutstandingBalanceAsync(Customer, Plant));
            }

            // ── ⑤ 收款（借 銀行存款 / 貸 應收帳款）──────────────────────────
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var receipt = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, RecvDate, PaymentMethod.Wire, null, 1m, gross, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, gross) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(receipt.ARReceiptId);

                var invoice = await db.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == arId);
                Assert.Equal(ARInvoiceStatus.Received, invoice.Status);
                Assert.Equal(gross, invoice.ReceivedAmount);
                Assert.Equal(0m, await Svc<IARInvoiceService>(scope).GetOutstandingBalanceAsync(Customer, Plant));
            }

            // ── ⑥ 全鏈收斂驗證 ─────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 應收帳款：開票借記、收款貸記 → 收斂為 0
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsReceivableCode));
                // 銷貨成本：出貨時借記（AVCO 120 × 40）
                Assert.Equal(cogs, Net(gl, StandardAccounts.CogsCode));
                // 銷貨收入：貸方為負（淨額＝借−貸）
                Assert.Equal(-revenue, Net(gl, StandardAccounts.SalesRevenueCode));
                // 存貨：期初入庫 12,000 − 出貨 4,800
                Assert.Equal(openingQty * openingCost - cogs,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(0m, gl.Values.Sum());

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditSoOverShipmentAsync(Plant));
                Assert.Empty(await audit.AuditShipmentInvoiceGapAsync(Plant));
                Assert.Empty(await audit.AuditSoShippedQtyDriftAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }
    }
}
