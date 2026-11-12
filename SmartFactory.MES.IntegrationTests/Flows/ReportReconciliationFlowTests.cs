using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §8 的 5-6：報表的數字要與單據明細加總一致。
    ///
    /// 報表是全站唯一「不經過過帳路徑」的一群查詢——每張各寫各的彙總，
    /// 沒有共用單點，所以它們與總帳之間的偏差不會被任何稽核抓到（稽核比的是單據與過帳，
    /// 不是報表與過帳）。使用者看到的就是一個數字，對不對得起來只能靠對帳。
    ///
    /// 本測試釘三組不變量：
    ///   ① 資產負債表逐科目 == 總帳該科目淨額，且 **資產 = 負債 + 權益 + 本期損益**
    ///   ② 損益表的收入／成本 == 總帳 4xxx／5xxx 淨額，且淨利與資產負債表的本期損益同一個數
    ///   ③ 應收帳齡的分桶合計 == 應收未結餘額 == 總帳應收帳款餘額
    ///
    /// ①的平衡式帶著本期損益：`BalanceSheetService` **刻意**不做期末結轉
    /// （損益科目 4~8 不併入權益，見 `RPT/FinancialStatementTests` 的既有契約），
    /// 這個系統也沒有任何 3xxx 權益科目與結轉分錄，所以權益恆為 0，
    /// 資產與負債的差額正是本期損益。服務層這樣做是對的——沒有結轉分錄就不該假裝有；
    /// 問題出在畫面只擺三張合計卡，看報表的人得到一組湊不起來又看不出少什麼的數字。
    /// 本測試釘的是「兩張報表算的是同一本帳」，畫面那半由控制器補上第四個數解決。
    /// </summary>
    [Collection("Integration")]
    public class ReportReconciliationFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW5R";

        private const string Fg = "FLOW-W5R-FG";
        private const string So = "FLOW-W5R-SO";
        private const string Gs = "FLOW-W5R-GS";

        private static readonly DateOnly StockDate = new(2095, 9, 1);
        private static readonly DateOnly ShipDate  = new(2095, 9, 10);
        private static readonly DateOnly MoneyDate = new(2095, 9, 20);
        private const string Period = "209509";

        private const decimal OpenQty = 100m, OpenCost = 120m;
        private const decimal ShipQty = 40m, Price = 300m, TaxRate = 0.05m;
        private const decimal Cogs    = ShipQty * OpenCost;   //  4,800
        private const decimal Revenue = ShipQty * Price;      // 12,000
        private const decimal Tax     = Revenue * TaxRate;    //    600
        private const decimal Gross   = Revenue + Tax;        // 12,600
        private const decimal PartPay = 5_000m;

        public ReportReconciliationFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Fg, "報表對帳測試成品")).GetAwaiter().GetResult();

        /// <summary>
        /// 資產負債表必須平：資產 = 負債 + 權益 + 本期損益。
        /// 不平就代表有東西被漏掉了，而漏掉的是什麼，看報表的人是看不出來的。
        /// </summary>
        [Fact]
        public async Task BalanceSheet_Balances_AndEveryLineMatchesTheLedger()
        {
            await SeedChainAsync();

            using var scope = NewScope(out var db);
            var report = await Svc<IBalanceSheetService>(scope).GetReportAsync(Period, Plant);
            var gl     = await GlNetByAccountAsync(db, Plant);

            // 逐科目：資產取借方正、負債取貸方正
            foreach (var item in report.Sections.Single(s => s.Category == "Assets").Items)
                Assert.Equal(Net(gl, item.AccountCode), item.Balance);
            foreach (var item in report.Sections.Single(s => s.Category == "Liabilities").Items)
                Assert.Equal(-Net(gl, item.AccountCode), item.Balance);

            // 1／2 開頭且有餘額的科目一個都不能漏
            foreach (var (account, net) in gl.Where(kv => net0(kv.Value)
                                                       && (kv.Key.StartsWith("1") || kv.Key.StartsWith("2"))))
                Assert.True(
                    report.Sections.SelectMany(s => s.Items).Any(i => i.AccountCode == account),
                    $"總帳有 {account}（淨額 {net}），資產負債表卻沒有這一列。");

            var pl = await Svc<IProfitLossService>(scope).GetReportAsync("", Period, Plant);
            Assert.Equal(
                report.TotalLiabilities + report.TotalEquity + pl.NetProfit,
                report.TotalAssets);

            // 這個系統沒有任何 3xxx 分錄，權益恆為 0；差額全在本期損益上
            Assert.Equal(0m, report.TotalEquity);
            Assert.Equal(Revenue - Cogs, pl.NetProfit);
        }

        /// <summary>
        /// 損益表與總帳一致，且它算出來的淨利必須恰好補平資產負債表的缺口。
        /// 兩張表各自從總帳彙總，符號慣例只要有一邊寫反就會各說各話。
        /// </summary>
        [Fact]
        public async Task ProfitLoss_MatchesLedger_AndAgreesWithBalanceSheetEquity()
        {
            await SeedChainAsync();

            using var scope = NewScope(out var db);
            var pl = await Svc<IProfitLossService>(scope).GetReportAsync(Period, Period, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);

            Assert.Equal(-Net(gl, StandardAccounts.SalesRevenueCode), pl.Revenue);
            Assert.Equal(Net(gl, StandardAccounts.CogsCode),          pl.Cogs);
            Assert.Equal(Revenue, pl.Revenue);
            Assert.Equal(Cogs,    pl.Cogs);

            // 兩張報表算的是同一本帳：資產減負債權益之後剩下的，必須恰好是損益表的淨利
            var bs = await Svc<IBalanceSheetService>(scope).GetReportAsync(Period, Plant);
            Assert.Equal(pl.NetProfit, bs.TotalAssets - bs.TotalLiabilities - bs.TotalEquity);
        }

        /// <summary>
        /// 應收帳齡：分桶合計 == 未結餘額 == 總帳應收帳款。
        /// 部分收款是這條最容易漏的地方——桶子裡放的必須是「未收的部分」，不是整張發票。
        /// </summary>
        [Fact]
        public async Task ArAging_BucketsSumToOutstanding_WhichMatchesTheLedger()
        {
            await SeedChainAsync();

            List<AgingRowVm> rows;
            using (var scope = NewScope(out var db))
            {
                var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
                var ctrl = new ARAgingController(
                    db,
                    sp.GetRequiredService<IUserContext>(),
                    sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IExportService>(),
                    sp.GetRequiredService<IDateTimeService>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext { RequestServices = sp },
                    },
                };

                var view = Assert.IsType<ViewResult>(
                    await ctrl.Index(MoneyDate, null, null, searched: true));
                rows = Assert.IsType<List<AgingRowVm>>(view.Model);
            }

            using (var scope = NewScope(out var db))
            {
                var agingTotal = rows.Sum(r => r.Total);
                var bucketSum  = rows.Sum(r => r.Current + r.Days30 + r.Days60 + r.Days90 + r.Days90Plus);
                var outstanding = await Svc<IARInvoiceService>(scope)
                    .GetOutstandingBalanceAsync(Customer, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                Assert.Equal(agingTotal, bucketSum);                       // 分桶不得漏桶
                Assert.Equal(outstanding, agingTotal);                     // 報表 == 子帳
                Assert.Equal(Gross - PartPay, agingTotal);                 // 只掛未收的那一段
                Assert.Equal(agingTotal, Net(gl, StandardAccounts.AccountsReceivableCode)); // 子帳 == 總帳
            }
        }

        /// <summary>
        /// 庫存餘額表：Σ(在庫量 × 單位成本) == 總帳存貨科目 1401。
        ///
        /// §12.4 原本把這張表列為「不納入自動化」，理由是它只是 Inventories 的直接投影、
        /// 而「明細帳合計 == 總帳」已被其他流程測試釘住。但那些測試釘的是 **Inventories**，
        /// 不是**這張報表讀出來的數字**——中間還隔著廠別條件、includeZero、
        /// 以及「單位成本從哪裡取」。報表自己算錯的話，前面那些測試一支都不會紅。
        /// </summary>
        [Fact]
        public async Task InventoryBalance_ValueMatchesTheLedger()
        {
            await SeedChainAsync();

            List<InventoryBalanceRowVm> rows;
            using (var scope = NewScope(out var db))
            {
                var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
                var ctrl = new InventoryBalanceController(
                    db,
                    sp.GetRequiredService<IUserContext>(),
                    sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IExportService>(),
                    sp.GetRequiredService<IUomService>(),
                    sp.GetRequiredService<ICurrencyService>(),
                    sp.GetRequiredService<SmartFactory.MES.Modules.INV.Services.IInventoryReservationService>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext { RequestServices = sp },
                    },
                };

                var view = Assert.IsType<ViewResult>(await ctrl.Index(
                    productCode: null, whCode: null, locationCode: null, lotNo: null,
                    includeZero: false, pageNumber: 1, pageSize: 1000,
                    sortOrder: null, searched: true));
                rows = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model).ToList();
            }

            using (var scope = NewScope(out var db))
            {
                var reportValue = rows.Sum(r => r.QtyOnHand * r.UnitCost);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 期初 100 顆進來、出貨走掉 40 顆，剩 60 顆 × 120 元
                Assert.Equal((OpenQty - ShipQty) * OpenCost, reportValue);
                Assert.Equal(Net(gl, StandardAccounts.RawMaterialInventoryCode), reportValue);
            }
        }

        /// <summary>
        /// 毛利表：收入與成本兩腿都要跟總帳同一個數，毛利才是真的毛利。
        ///
        /// 這張表**不是**從總帳算的——收入取出貨明細、成本取 GS 出庫異動的 TotalCost，
        /// 兩條路徑都繞過了傳票。所以它與總帳之間沒有任何共用單點，
        /// 偏掉的時候畫面上就只是一個比較好看（或比較難看）的毛利率，沒有任何地方會叫。
        /// </summary>
        [Fact]
        public async Task GrossMargin_BothLegsMatchTheLedger()
        {
            await SeedChainAsync();

            using var scope = NewScope(out var db);
            var report = await Svc<IGrossMarginService>(scope)
                .GetReportAsync(StockDate, MoneyDate, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);

            Assert.Equal(Revenue, report.TotalRevenue);
            Assert.Equal(Cogs,    report.TotalCost);

            // 收入是貸方餘額，取負號才是正的收入；成本是借方餘額
            Assert.Equal(-Net(gl, StandardAccounts.SalesRevenueCode), report.TotalRevenue);
            Assert.Equal(Net(gl, StandardAccounts.CogsCode),          report.TotalCost);

            // 毛利與毛利率是衍生值，一併釘住免得日後改公式沒人發現
            Assert.Equal(Revenue - Cogs, report.TotalGrossProfit);
            Assert.Equal(Math.Round((Revenue - Cogs) / Revenue * 100, 2), report.TotalMarginRate);

            // 逐品號也要對得上總計（分組漏品號時總計仍可能碰巧相同）
            Assert.Equal(report.TotalGrossProfit, report.Rows.Sum(r => r.GrossProfit));
        }

        private static bool net0(decimal v) => v != 0m;

        // ── 佈景：期初入庫 → 出貨過帳 → AR 開票 → 部分收款 ─────────────────

        private async Task SeedChainAsync()
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-W5R-OPEN", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "",
                    Qty = OpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = OpenCost,
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
                    SalesOrderId = So, OrderDate = ShipDate, CompanyCode = Customer,
                    CreatedBy = UserId, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                so.Lines.Add(new SalesOrderLine
                {
                    SalesOrderId = So, LineNo = 1, ProductCode = Fg,
                    OrderQty = ShipQty, UomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
                });
                db.SalesOrders.Add(so);
                await db.SaveChangesAsync();
            }

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
                    ProductCode = Fg, ShipQty = ShipQty, StockQty = ShipQty,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
                    LineAmount = Revenue, TaxAmount = Tax, TotalAmount = Gross,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.GoodsShipments.Add(ship);

                var soLine = await db.SalesOrderLines.IgnoreQueryFilters()
                    .SingleAsync(l => l.SalesOrderId == So && l.LineNo == 1);
                soLine.ShippedQty += ShipQty;
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = Gs, MovementDate = ShipDate,
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

            string arId;
            using (var scope = NewScope(out var db))
                arId = (await Svc<IARInvoiceService>(scope).GenerateFromShipmentAsync(Gs)).ARInvoiceId;

            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var rcpt = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, MoneyDate, PaymentMethod.Wire, null, 1m, PartPay, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, PartPay) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(rcpt.ARReceiptId);
            }
        }
    }
}
