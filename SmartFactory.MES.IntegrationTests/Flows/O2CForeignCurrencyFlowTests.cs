using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §6.1 Wave 3 的 3-7：以外幣訂單重跑 3-1 ~ 3-5，驗換算鏈一致。
    ///
    /// 為什麼要與 <see cref="P2PForeignCurrencyFlowTests"/> 分開：那一支把買匯與賣匯**設成同一個數字**
    /// （`BuyRate = rate, SellRate = rate`），所以「取錯匯率型別」這一類缺陷在它那裡完全看不見。
    /// 銷售鏈整條走賣出匯率（SO／出貨／銷退／收款都明寫 <c>RateType.Sell</c>），
    /// 只要有一段退回預設值（Buy），畫面上的本幣金額與帳上的本幣金額就會分家。
    ///
    /// 幣別代碼用 ZZS（不是 P2P 那支的 ZZU）：兩支的買賣匯率不同，共用代碼會互相覆寫。
    /// 幣別與匯率是**沒有廠別欄位**的全域主檔，故一律先查後補、且不在 Dispose 刪除。
    /// </summary>
    [Collection("Integration")]
    public class O2CForeignCurrencyFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZO2CF";

        private const string Fg  = "FLOW-O2CF-FG";
        private const string So  = "FLOW-O2CF-SO";
        private const string Cat = "FLOW-O2CF-CAT";
        private const string Fx  = "ZZS";

        private static readonly DateOnly StockDate = new(2099, 6, 1);
        private static readonly DateOnly ShipDate  = new(2099, 6, 10);
        private static readonly DateOnly RecvDate  = new(2099, 6, 25);

        // 買賣匯率刻意不同，且兩個日期都不同——四個數字互不相等，
        // 任何一段取錯型別或取錯日期都會產生獨一無二的錯誤金額。
        private const decimal ShipBuy  = 30m;
        private const decimal ShipSell = 31m;
        private const decimal RecvBuy  = 32m;
        private const decimal RecvSell = 33m;

        private const decimal OpeningQty  = 100m;
        private const decimal OpeningCost = 120m;    // 本幣成本（存貨與 COGS 與外幣無關）
        private const decimal ShipQty     = 40m;
        private const decimal PriceFx     = 100m;    // 外幣售價
        private const decimal TaxRate     = 0.05m;

        private const decimal NetFx   = ShipQty * PriceFx;            // 4,000
        private const decimal GrossFx = NetFx * (1m + TaxRate);       // 4,200

        public O2CForeignCurrencyFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "外幣出貨流程成品"));

            using var scope = NewScope(out var db);

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Cat, CategoryName = "外幣出貨流程類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                    DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });

            if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                db.Currencies.Add(new Currency
                {
                    CurrencyCode = Fx, CurrencyName = "流程測試銷售外幣",
                    Symbol = "S$", DecimalPlaces = 2, IsActive = true,
                });

            foreach (var (date, buy, sell) in new[]
                     { (ShipDate, ShipBuy, ShipSell), (RecvDate, RecvBuy, RecvSell) })
                if (!await db.CurrencyRates.AsNoTracking()
                        .AnyAsync(r => r.CurrencyCode == Fx && r.RateDate == date))
                    db.CurrencyRates.Add(new CurrencyRate
                    {
                        CurrencyCode = Fx, RateDate = date, BuyRate = buy, SellRate = sell,
                    });

            await db.SaveChangesAsync();

            var p = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == Fg);
            p.CategoryCode = Cat;
            await db.SaveChangesAsync();

            // 幣別清單有 10 分鐘快取；不清的話小數位會靜靜退回預設值。
            Svc<ICurrencyService>(scope).InvalidateCache();
        }

        // ── 前置與入口 ──────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync()
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = "FLOW-O2CF-OPEN", LineNo = 1,
                ProductCode = Fg, LocationCode = Loc, LotNo = "",
                Qty = OpeningQty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                UnitPrice = OpeningCost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedSalesOrderAsync()
        {
            using var scope = NewScope(out var db);
            var so = new SalesOrder
            {
                SalesOrderId = So, OrderDate = ShipDate, CompanyCode = Customer,
                CreatedBy = UserId, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = So, LineNo = 1, ProductCode = Fg,
                OrderQty = ShipQty, UomCode = Uom,
                StockQty = ShipQty, StockUomCode = Uom,
                UnitPrice = PriceFx, CurrencyCode = Fx, ExchangeRate = ShipSell, TaxRate = TaxRate,
            });
            db.SalesOrders.Add(so);
            await db.SaveChangesAsync();
        }

        private GoodsShipmentController NewShipmentController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new GoodsShipmentController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        private async Task<string> ShipAsync()
        {
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewShipmentController(scope, db);
                var vm = new GoodsShipmentFormVm
                {
                    ShipDate = ShipDate, CompanyCode = Customer, SalesOrderId = So, CreatedBy = UserId,
                    Lines =
                    {
                        new GoodsShipmentLineVm
                        {
                            LineNo = 1, SalesOrderId = So, SoLineNo = 1, ProductCode = Fg,
                            ShipQty = ShipQty, UomCode = Uom, StockUomCode = Uom,
                            UnitPrice = PriceFx, CurrencyCode = Fx,
                            // 前端帶什麼都不重要：ResolveLineRatesAsync 會以後端權威賣出匯率覆寫。
                            ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = NetFx, TaxAmount = NetFx * TaxRate, TotalAmount = GrossFx,
                        },
                    },
                };
                var result = await ctrl.Save(vm);
                Assert.True(result is ContentResult, "外幣出貨存檔應成功：" + string.Join("；",
                    ctrl.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
            }

            string gs;
            using (var scope = NewScope(out var db))
                gs = await db.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.GoodsShipmentId).SingleAsync();

            using (var scope = NewScope(out _))
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "true");
            try
            {
                using var scope = NewScope(out var db);
                var ctrl = NewShipmentController(scope, db);
                var result = await ctrl.Confirm(gs);
                Assert.True(result is OkObjectResult,
                    $"外幣出貨確認應成功：{(result as BadRequestObjectResult)?.Value}");
            }
            finally
            {
                using var scope = NewScope(out _);
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, null);
            }
            return gs;
        }

        // ── 3-7 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task ForeignCurrencySale_UsesSellRateEndToEnd_AndFxDifferenceOnlyComesFromReceiptDate()
        {
            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync();
            var gs = await ShipAsync();

            string arId;
            using (var scope = NewScope(out var db))
            {
                var shipLine = await db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsShipmentId == gs && l.LineNo == 1);

                // 出貨單本身：後端以賣出匯率覆寫，本幣金額 = 交易幣含稅 × 賣匯
                Assert.Equal(ShipSell, shipLine.ExchangeRate);
                Assert.Equal(GrossFx * ShipSell, shipLine.AmountLocal);

                var invoice = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                arId = invoice.ARInvoiceId;

                Assert.Equal(Fx, invoice.CurrencyCode);
                Assert.Equal(GrossFx, invoice.TotalAmount);

                // 核心判準：同一筆銷售只能有**一個**本幣金額。
                // 開票若退回預設的買入匯率，出貨單畫面上的本幣合計與帳上的應收就此分家，
                // 而且差額不會落在任何科目——它會被收款當下的匯兌損益吸收掉（見下方），
                // 所以總帳照樣平、應收照樣歸零，沒有任何既有檢查會叫。
                Assert.Equal(ShipSell, invoice.ExchangeRate);
                Assert.Equal(shipLine.AmountLocal, invoice.AmountLocal);

                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(-NetFx * ShipSell, Net(gl, StandardAccounts.SalesRevenueCode));
                Assert.Equal(-NetFx * TaxRate * ShipSell, Net(gl, StandardAccounts.OutputVatCode));
                Assert.Equal(GrossFx * ShipSell, Net(gl, StandardAccounts.AccountsReceivableCode));

                // 存貨與銷貨成本是本幣世界的事，與匯率無關
                Assert.Equal(ShipQty * OpeningCost, Net(gl, StandardAccounts.CogsCode));
                Assert.Equal((OpeningQty - ShipQty) * OpeningCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));
            }

            // 收款：以收款日賣匯結清，差額只能來自「開票日 → 收款日」的匯率變動
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var receipt = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, RecvDate, PaymentMethod.Wire, Fx, RecvSell, GrossFx, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, GrossFx) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(receipt.ARReceiptId);

                var confirmed = await db.ARReceipts.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(r => r.ARReceiptId == receipt.ARReceiptId);
                Assert.Equal(GrossFx * (RecvSell - ShipSell), confirmed.ExchangeGainLoss);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsReceivableCode));
                Assert.Equal(-GrossFx * (RecvSell - ShipSell), Net(gl, StandardAccounts.FxGainCode));
                Assert.Equal(0m, Net(gl, StandardAccounts.FxLossCode));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditShipmentInvoiceGapAsync(Plant));
                Assert.Empty(await audit.AuditSoShippedQtyDriftAsync(Plant));
            }
        }
    }
}
