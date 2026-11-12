using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-2：已認列的 AP／AR 只能作廢回沖，不能刪除。
    ///
    /// 作業書的通過標準只寫「確認回沖傳票正確產生」。乾淨的那一半（沒收過錢的發票
    /// 作廢後所有科目歸零）確實成立，但「正確」在這裡還有一個更難的邊界：
    /// **發票已經收過一部分錢的時候**。
    ///
    /// AP 認列是 借 2152＋1155 ／ 貸 2100；付款是 借 2100 ／ 貸 1130。
    /// 作廢的回沖是把整張認列分錄借貸對調，也就是 借 2100 全額。
    /// 若此時已經付過一部分，那筆付款的 借 2100 還留著——兩筆借方加起來超過原本的貸方，
    /// 應付帳款就變成**借餘**：錢付出去了，帳上卻沒有任何負債對應得上，
    /// 而且再也不會有東西來沖它。AR 的收款端是完全對稱的同一個洞。
    ///
    /// 服務層原本只擋 <c>Paid</c>／<c>Received</c>（全額付清），
    /// 但那道守門自己的錯誤訊息就寫著「請先沖銷付款」——意思顯然是
    /// 「只要有核銷過就得先沖掉」，而不是「付到剛好滿額才要」。
    /// 部分付款的發票被放行，是這個判斷少了一個狀態。
    /// </summary>
    [Collection("Integration")]
    public class InvoiceVoidFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW4V";

        private const string Mat = "FLOW-W4V-MAT";
        private const string Fg  = "FLOW-W4V-FG";
        private const string Po  = "FLOW-W4V-PO";
        private const string So  = "FLOW-W4V-SO";
        private const string Gs  = "FLOW-W4V-GS";

        private static readonly DateOnly OrderDate = new(2098, 7, 1);
        private static readonly DateOnly DealDate  = new(2098, 7, 10);
        private static readonly DateOnly MoneyDate = new(2098, 7, 20);

        private const decimal Qty = 100m, Price = 250m, TaxRate = 0.05m;
        private const decimal Net = Qty * Price;          // 25,000
        private const decimal Tax = Net * TaxRate;        //  1,250
        private const decimal Gross = Net + Tax;          // 26,250

        private const decimal OpenCost = 120m, ShipQty = 40m, SalePrice = 300m;
        private const decimal Revenue = ShipQty * SalePrice;      // 12,000
        private const decimal SalesTax = Revenue * TaxRate;       //    600
        private const decimal SalesGross = Revenue + SalesTax;    // 12,600

        public InvoiceVoidFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "作廢流程測試原料"), (Fg, "作廢流程測試成品")).GetAwaiter().GetResult();

        /// <summary>
        /// 未付款的應付發票作廢：認列的每一個科目都必須回到作廢前的狀態。
        /// 特別是 2152 進貨暫估應付——它在入庫時被貸記、認列時被借記，
        /// 作廢等於把認列那一腿收回去，於是它必須重新掛回「貨到了、帳還沒認」的餘額，
        /// 而不是連同入庫那一腿一起消失。
        /// </summary>
        [Fact]
        public async Task VoidingUnpaidApInvoice_RestoresEveryAccountToPreAccrualState()
        {
            var grId = await SeedReceiptAsync();

            Dictionary<string, decimal> beforeAccrual;
            using (var scope = NewScope(out var db))
                beforeAccrual = await GlNetByAccountAsync(db, Plant);

            string apId;
            using (var scope = NewScope(out var db))
                apId = (await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, Qty)).APInvoiceId;

            using (var scope = NewScope(out var db))
                await Svc<IAPInvoiceService>(scope).VoidAsync(apId);

            using (var scope = NewScope(out var db))
            {
                var inv = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.Void, inv.Status);

                var after = await GlNetByAccountAsync(db, Plant);
                foreach (var account in beforeAccrual.Keys.Union(after.Keys))
                    Assert.Equal(Net(beforeAccrual, account), Net(after, account));

                // 認列前的狀態：貨已入庫、帳未認 → 暫估應付掛著未稅金額
                Assert.Equal(-Net, Net(after, StandardAccounts.GrIrAPClearingCode));
                Assert.Equal(0m,   Net(after, StandardAccounts.AccountsPayableCode));
                Assert.Equal(0m,   Net(after, StandardAccounts.InputVatCode));

                await AssertEveryEntryBalancedAsync(db, Plant);
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditJournalBalanceAsync(Plant));
            }
        }

        /// <summary>
        /// 已部分付款的應付發票不得作廢——必須先把付款單沖掉。
        /// 放行的話錢已經出去、負債卻被整筆回沖，應付帳款會留下一筆永遠沒人認領的借餘。
        /// </summary>
        [Fact]
        public async Task VoidingPartiallyPaidApInvoice_IsRejectedUntilThePaymentIsReversed()
        {
            var grId = await SeedReceiptAsync();

            string apId;
            using (var scope = NewScope(out var db))
                apId = (await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, Qty)).APInvoiceId;

            string pmtId;
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var pmt = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, MoneyDate, PaymentMethod.Wire, null, 1m, Gross / 2m, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, Gross / 2m) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(pmt.APPaymentId);
                pmtId = pmt.APPaymentId;

                var inv = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.PartialPaid, inv.Status);
            }

            using (var scope = NewScope(out var db))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Svc<IAPInvoiceService>(scope).VoidAsync(apId));
                PzAssert.Message("Fin_PaidInvoiceCannotVoid", ex.Message);
            }

            // 被擋下之後發票必須還活著：擋門不得留下半套狀態
            using (var scope = NewScope(out var db))
            {
                var inv = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.APInvoiceId == apId);
                Assert.Equal(APInvoiceStatus.PartialPaid, inv.Status);
            }

            // 沖掉付款之後才作廢得了，且應付帳款回到零
            using (var scope = NewScope(out var db))
                await Svc<IFinPaymentService>(scope).VoidAPPaymentAsync(pmtId);
            using (var scope = NewScope(out var db))
                await Svc<IAPInvoiceService>(scope).VoidAsync(apId);

            using (var scope = NewScope(out var db))
            {
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(-Net, Net(gl, StandardAccounts.GrIrAPClearingCode));
                await AssertEveryEntryBalancedAsync(db, Plant);
            }
        }

        /// <summary>
        /// 已部分收款的應收發票不得作廢。與 AP 完全對稱：
        /// 錢已經收進銀行，應收卻被整筆回沖，應收帳款會留下永遠沒人沖的貸餘。
        /// </summary>
        [Fact]
        public async Task VoidingPartiallyReceivedArInvoice_IsRejectedUntilTheReceiptIsReversed()
        {
            await SeedShipmentAsync();

            string arId;
            using (var scope = NewScope(out var db))
                arId = (await Svc<IARInvoiceService>(scope).GenerateFromShipmentAsync(Gs)).ARInvoiceId;

            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var rcpt = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, MoneyDate, PaymentMethod.Wire, null, 1m, SalesGross / 2m, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, SalesGross / 2m) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(rcpt.ARReceiptId);

                var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.ARInvoiceId == arId);
                Assert.Equal(ARInvoiceStatus.PartialReceived, inv.Status);
            }

            using (var scope = NewScope(out var db))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Svc<IARInvoiceService>(scope).VoidAsync(arId));
                PzAssert.Message("Fin_ReceivedInvoiceCannotVoid", ex.Message);
            }

            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.ARInvoiceId == arId);
                Assert.Equal(ARInvoiceStatus.PartialReceived, inv.Status);
            }
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        /// <summary>採購單 → 進貨單 → 入庫過帳（貸 2152），停在「貨到了、帳未認」。</summary>
        private async Task<string> SeedReceiptAsync()
        {
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
                    OrderQty = Qty, UomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            string grId;
            using (var scope = NewScope(out var db))
            {
                var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = DealDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = Qty, StockQty = Qty,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                            TaxRate = TaxRate, LineAmount = Net, TotalAmount = Gross,
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
                    PlantId = Plant, DocNo = grId, LineNo = line.LineNo,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = line.StockQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = DealDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }

            return grId;
        }

        /// <summary>期初入庫 → 銷貨訂單 → 出貨過帳，停在「貨出了、帳未開」。</summary>
        private async Task SeedShipmentAsync()
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-W4V-OPEN", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "",
                    Qty = 100m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = OrderDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = OpenCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var so = new SalesOrder
                {
                    SalesOrderId = So, OrderDate = DealDate, CompanyCode = Customer,
                    CreatedBy = UserId, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                so.Lines.Add(new SalesOrderLine
                {
                    SalesOrderId = So, LineNo = 1, ProductCode = Fg,
                    OrderQty = ShipQty, UomCode = Uom, UnitPrice = SalePrice, TaxRate = TaxRate,
                });
                db.SalesOrders.Add(so);
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var ship = new GoodsShipment
                {
                    GoodsShipmentId = Gs, ShipDate = DealDate, TypeCode = "GS",
                    CompanyCode = Customer, SalesOrderId = So, CreatedBy = UserId,
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                ship.Lines.Add(new GoodsShipmentLine
                {
                    GoodsShipmentId = Gs, LineNo = 1, SalesOrderId = So, SoLineNo = 1,
                    ProductCode = Fg, ShipQty = ShipQty, StockQty = ShipQty,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = SalePrice, TaxRate = TaxRate,
                    LineAmount = Revenue, TaxAmount = SalesTax, TotalAmount = SalesGross,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.GoodsShipments.Add(ship);

                var soLine = await db.SalesOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.SalesOrderId == So && l.LineNo == 1);
                soLine.ShippedQty += ShipQty;
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Gs, MovementDate = DealDate,
                    TypeCode = "GS", TypeCategory = "Sales", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Fg, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = ShipQty, UomCode = Uom, SetQtyPutAway = _ => { },
                        }
                    },
                    SetDocPosted = () => ship.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }
        }
    }
}
