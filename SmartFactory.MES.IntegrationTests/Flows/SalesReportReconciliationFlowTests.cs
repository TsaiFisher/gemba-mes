using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 報表 × 單據明細的**跨層對帳**（manual-test-plan.md 5-6）。
    ///
    /// 既有的 RPT 測試驗的是「報表內部算得對」——給它幾筆種子資料，檢查彙總與比率沒算錯。
    /// 沒有人驗過的是另一件事：**報表撈到的那批單據，跟真正跑完流程留下來的單據是不是同一批**。
    /// 這兩件事會分岔，因為報表各腿的篩選條件是各自寫的：
    /// 營收腿看 `GoodsShipment.Status`、成本腿看 `StockMovement`，
    /// 而單據狀態會隨流程往前走而改變——某個狀態一旦被漏掉，報表不會報錯，只會少一半。
    ///
    /// 本檔跑完一條真實的出貨鏈（過帳後出貨單停在 <c>Posted</c>），
    /// 再拿三張銷售報表去對「出貨明細加總」與「庫存異動成本加總」。
    /// </summary>
    [Collection("Integration")]
    public class SalesReportReconciliationFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZRPT";

        private const string Fg = "FLOW-RPT-FG";

        private static readonly DateOnly StockDate = new(2097, 7, 1);
        private static readonly DateOnly ShipDate  = new(2097, 7, 10);
        private static readonly DateOnly From      = new(2097, 7, 1);
        private static readonly DateOnly To        = new(2097, 7, 31);

        private const decimal OpeningQty  = 100m;
        private const decimal OpeningCost = 120m;
        private const decimal ShipQty     = 40m;
        private const decimal Price       = 300m;
        private const decimal TaxRate     = 0.05m;

        private const string Gs = "RPT-GS-1";

        private static decimal Revenue => ShipQty * Price;      // 12,000
        private static decimal Cogs    => ShipQty * OpeningCost; //  4,800

        public SalesReportReconciliationFlowTests(TestWebApplicationFactory factory) : base(factory)
        {
            ResetAsync((Fg, "流程測試報表品")).GetAwaiter().GetResult();
            ShipAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 毛利表的兩腿必須取到**同一批**出貨：營收＝出貨明細加總、成本＝該出貨的庫存異動加總。
        ///
        /// 這支測試存在的理由：營收腿原本只認 <c>DocStatus.Confirmed</c>，
        /// 但出貨過帳完成後狀態是 <c>Posted</c>——於是完成的出貨有成本沒營收、
        /// 未完成的出貨有營收沒成本，**兩腿永遠不會同時到齊**，毛利率因此恆為 100% 或整列消失。
        /// </summary>
        [Fact]
        public async Task GrossMargin_PostedShipment_RevenueAndCostBothReconcileToDocuments()
        {
            using var scope = NewScope(out var db);

            var report = await Svc<IGrossMarginService>(scope).GetReportAsync(From, To, Plant);
            var row = Assert.Single(report.Rows);

            Assert.Equal(await ShipmentLineTotalAsync(db), row.Revenue);
            Assert.Equal(await SalesMovementCostAsync(db), row.Cost);

            Assert.Equal(Revenue, row.Revenue);
            Assert.Equal(Cogs,    row.Cost);
            Assert.Equal(1,       row.ShipCount);
        }

        /// <summary>貢獻邊際的營收腿與毛利表取自同一處，同樣不得漏掉已過帳的出貨。</summary>
        [Fact]
        public async Task ContributionMargin_PostedShipment_RevenueReconcilesToShipmentLines()
        {
            using var scope = NewScope(out var db);

            var report = await Svc<IContributionMarginService>(scope).BuildAsync(From, To, Plant);

            Assert.NotEmpty(report.Rows);
            Assert.Equal(await ShipmentLineTotalAsync(db), report.Rows.Sum(r => r.Revenue));
        }

        /// <summary>多維度毛利分析同上；三張報表共用同一個篩選條件，錯一起錯。</summary>
        [Fact]
        public async Task MarginAnalysis_PostedShipment_RevenueReconcilesToShipmentLines()
        {
            using var scope = NewScope(out var db);

            var report = await Svc<IMarginAnalysisService>(scope)
                .GetReportAsync(From, To, Plant, "Product");

            Assert.NotEmpty(report.Rows);
            Assert.Equal(await ShipmentLineTotalAsync(db), report.Rows.Sum(r => r.Revenue));
        }

        /// <summary>
        /// 應收未結餘額（帳齡報表的分母）＝發票子帳的 TotalAmount − ReceivedAmount。
        /// 這一腿本來就是對的，放在這裡是為了讓「報表 vs 明細」這條對帳有個正向對照組——
        /// 上面三支紅的時候，可以確認紅的是報表篩選而不是這條流程本身沒跑起來。
        /// </summary>
        [Fact]
        public async Task ArOutstanding_ReconcilesToInvoiceSubledger()
        {
            using var scope = NewScope(out var db);

            var ar = Svc<IARInvoiceService>(scope);
            await ar.GenerateFromShipmentAsync(Gs);

            var expected = await db.ARInvoices.IgnoreQueryFilters()
                .Where(i => i.PlantId == Plant && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void)
                .SumAsync(i => i.TotalAmount - i.ReceivedAmount);

            Assert.Equal(expected, await ar.GetOutstandingBalanceAsync(Customer, Plant));
            Assert.Equal(Revenue + Revenue * TaxRate, expected);
        }

        // ── 對照用的「單據明細加總」──────────────────────────────────────
        //    刻意不重算一次業務公式，而是直接把單據與異動加總起來：
        //    報表要對的就是這兩個數字，不是我在測試裡另外算的版本。

        private async Task<decimal> ShipmentLineTotalAsync(SmartFactory.MES.Data.ApplicationDbContext db)
            => await db.GoodsShipmentLines.IgnoreQueryFilters()
                .Where(l => l.GoodsShipment!.PlantId == Plant
                         && l.GoodsShipment.ShipDate >= From && l.GoodsShipment.ShipDate <= To
                         && l.GoodsShipment.Status != DocStatus.Draft
                         && l.GoodsShipment.Status != DocStatus.Void)
                .SumAsync(l => l.LineAmount);

        private async Task<decimal> SalesMovementCostAsync(SmartFactory.MES.Data.ApplicationDbContext db)
            => await db.StockMovements.IgnoreQueryFilters()
                .Where(m => m.PlantId == Plant && m.TypeCode == "GS" && m.Direction == -1
                         && m.MovementDate >= From && m.MovementDate <= To)
                .SumAsync(m => (decimal?)m.TotalCost) ?? 0m;

        // ── 流程 ────────────────────────────────────────────────────────────

        /// <summary>期初入庫 → 建立出貨單 → 過帳（狀態走到 Posted，與正式流程一致）。</summary>
        private async Task ShipAsync()
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "RPT-OPEN-1", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "",
                    Qty = OpeningQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = OpeningCost, CreatedBy = UserId,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var tax = Revenue * TaxRate;
                var ship = new GoodsShipment
                {
                    GoodsShipmentId = Gs, ShipDate = ShipDate, TypeCode = "GS",
                    CompanyCode = Customer, CreatedBy = UserId, CreatedByRoleId = RoleId,
                    PlantId = Plant, Status = DocStatus.Draft,
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                };
                ship.Lines.Add(new GoodsShipmentLine
                {
                    GoodsShipmentId = Gs, LineNo = 1, ProductCode = Fg,
                    ShipQty = ShipQty, StockQty = ShipQty, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = Price, TaxRate = TaxRate,
                    LineAmount = Revenue, TaxAmount = tax, TotalAmount = Revenue + tax,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.GoodsShipments.Add(ship);
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
                            Qty = ShipQty, UomCode = Uom, StockUomCode = Uom, SetQtyPutAway = _ => { },
                        },
                    },
                    SetDocPosted = () => ship.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();

                Assert.Equal(DocStatus.Posted, ship.Status);   // 前提：完成的出貨是 Posted 不是 Confirmed
            }
        }
    }
}
