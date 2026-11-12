using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 存貨淨變現價值（NRV）評估以期末當時的帳面成本與數量計（2026-10-24）。
    /// 原本：① 帳面成本一律取 Inventory.UnitCost——FIFO 料該欄恆為 0，永遠不會提列跌價；
    /// ② 數量取現在的庫存；③ 售價窗是今天往前 90 天。反關帳重做上一期，三者都算錯。
    /// </summary>
    [Collection("Integration")]
    public class NrvAssessmentFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZNRV";
        private const string Period = "209703";
        private const string FifoCat = "FLOW-NRV-FIFO", Fifo = "FLOW-NRV-FMAT", Ma = "FLOW-NRV-MAT", RevAcc = "4991";
        private static readonly DateOnly End = new(2097, 3, 31);

        public NrvAssessmentFlowTests(TestWebApplicationFactory factory) : base(factory)
        {
            ResetAsync((Ma, "NRV 均價料")).GetAwaiter().GetResult();
            using var scope = NewScope(out var db);
            db.InventoryNrvAssessments.RemoveRange(db.InventoryNrvAssessments.IgnoreQueryFilters().Where(a => a.PlantId == Plant));
            db.AccountingPeriods.RemoveRange(db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PlantId == Plant));
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId = Period, PlantId = Plant, Year = 2097, Month = 3,
                StartDate = new DateOnly(2097, 3, 1), EndDate = End, Status = PeriodStatus.Reopened,
            });
            if (!db.ProductCategories.IgnoreQueryFilters().Any(c => c.CategoryCode == FifoCat))
                db.ProductCategories.Add(new ProductCategory { CategoryCode = FifoCat, CategoryName = "NRV FIFO", CostMethod = CostMethod.Fifo, IsActive = true });
            if (!db.Products.IgnoreQueryFilters().Any(p => p.ProductCode == Fifo))
                db.Products.Add(new Product { ProductCode = Fifo, ProductName = "NRV FIFO 料", CategoryCode = FifoCat, PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true });
            if (!db.Set<ChartOfAccount>().Any(a => a.AccountCode == RevAcc))
                db.Set<ChartOfAccount>().Add(new ChartOfAccount { AccountCode = RevAcc, AccountName = "NRV 測試收入", AccountType = AccountType.Revenue, AllowManualEntry = true, IsActive = true });
            db.SaveChanges();
        }

        [Fact]
        public async Task FIFO料與均價料_以期末當時數量與期末前90天售價評估跌價()
        {
            await PostAsync("FLOW-NRV-F1", Fifo, 10m, 100m, new DateOnly(2097, 3, 10));   // FIFO 10 @100
            await PostAsync("FLOW-NRV-M1", Ma, 10m, 100m, new DateOnly(2097, 3, 10));     // 均價 10 @100
            await PostAsync("FLOW-NRV-F2", Fifo, 30m, 100m, new DateOnly(2097, 4, 5));    // 期末之後才進的
            await PostAsync("FLOW-NRV-M2", Ma, 30m, 100m, new DateOnly(2097, 4, 5));
            await SaleAsync(new DateOnly(2097, 3, 20), 50m);                               // 期末前 90 天內：售價 50
            await SaleAsync(new DateOnly(2097, 4, 20), 500m);                              // 期末之後：不得採用

            using var scope = NewScope(out var db);
            var rows = await Svc<INrvAssessmentService>(scope).RunAssessmentAsync(Period, Plant);

            // NRV＝50 − 2% 銷售費用 1＝49；跌價＝(100 − 49) × 期末數量 10＝510
            foreach (var code in new[] { Fifo, Ma })
            {
                var r = Assert.Single(rows, x => x.ProductCode == code);
                Assert.Equal(10m, r.QtyOnHand);
                Assert.Equal(100m, r.CarryingCost);
                Assert.Equal(50m, r.NrvPrice);
                Assert.Equal(510m, r.ImpairmentLoss);
            }
        }

        private async Task PostAsync(string doc, string product, decimal qty, decimal price, DateOnly date)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = product, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase", MovementDate = date,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();
        }

        /// <summary>兩料各賣 1 個（帶品號的手動應收發票，NRV 售價來源）。</summary>
        private async Task SaleAsync(DateOnly date, decimal price)
        {
            using var scope = NewScope(out var db);
            var inv = new ARInvoice
            {
                CustomerCode = Customer, PlantId = Plant, InvoiceDate = date, DueDate = date.AddDays(30),
                ExchangeRate = 1m, TotalAmount = price * 2, AmountLocal = price * 2,
            };
            inv.Lines.Add(new ARInvoiceLine { LineNo = 1, AccountCode = RevAcc, ProductCode = Fifo, UomCode = Uom, Qty = 1m, UnitPrice = price, LineAmount = price });
            inv.Lines.Add(new ARInvoiceLine { LineNo = 2, AccountCode = RevAcc, ProductCode = Ma, UomCode = Uom, Qty = 1m, UnitPrice = price, LineAmount = price });
            await Svc<IARInvoiceService>(scope).CreateManualAsync(inv, DocumentApprovalStatus.Approved);
        }
    }
}
