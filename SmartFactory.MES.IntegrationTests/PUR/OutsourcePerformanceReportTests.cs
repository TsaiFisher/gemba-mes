using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.PUR
{
    /// <summary>
    /// 委外加工對帳與損耗分析表（roadmap-fin-cst-sub.md §5.3）服務層端到端驗證。
    ///
    /// 廠別 ZZOPR：供應商 OPR-SUP，委外採購單預交日 2099-07-10。
    ///   R1 收貨 2099-07-05（準時）Posted：成品 100、加工費 5000、超耗扣款 250。
    ///   R2 收貨 2099-07-15（逾期）Confirmed：成品 80、加工費 4000、超耗扣款 0。
    ///   R3 收貨 2099-07-08 Draft（應被排除，若誤入則成品/金額會爆量）。
    /// 期望供應商列：收貨 2 筆、準時率 50%（1/2）、收回 180、加工費 9000、超耗 250、超耗佔比 2.78%。
    /// 鑑別力：Draft 誤入→數字全爆；準時率分母/分子錯→≠50%；超耗未加總→≠250。
    /// </summary>
    [Collection("Integration")]
    public class OutsourcePerformanceReportTests
    {
        private const string Plant    = "ZZOPR";
        private const string Supplier = "OPR-SUP";
        private const string Opo      = "OPR-OPO";

        private readonly TestWebApplicationFactory _factory;

        public OutsourcePerformanceReportTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task GetReportAsync_AggregatesBySupplier_ExcludesDraft_ComputesOnTimeAndScrap()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IOutsourcePerformanceService>();

            var report = await svc.GetReportAsync(new DateOnly(2099, 7, 1), new DateOnly(2099, 7, 31), Plant);

            var row = Assert.Single(report.Rows);
            Assert.Equal(Supplier, row.SupplierCode);
            Assert.Equal("委外廠甲", row.SupplierName);
            Assert.Equal(2, row.ReceiptCount);          // R3 Draft 排除
            Assert.Equal(2, row.EvaluableCount);
            Assert.Equal(1, row.OnTimeCount);
            Assert.Equal(50m, row.OnTimeRate);
            Assert.Equal(180m, row.ReceivedQty);
            Assert.Equal(9000m, row.ProcessingFee);
            Assert.Equal(250m, row.ScrapPenalty);
            Assert.Equal(2.78m, row.ScrapPenaltyRate);  // 250/9000*100

            Assert.Equal(250m, report.TotalScrapPenalty);
            Assert.Equal(50m, report.TotalOnTimeRate);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Purge(db);

            db.Companies.Add(new Company
            {
                CompanyCode = Supplier, CompanyName = "委外廠甲", IsSupplier = true, IsActive = true,
            });

            db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier,
                OrderDate = new DateOnly(2099, 7, 1), ExpectedDeliveryDate = new DateOnly(2099, 7, 10),
                Status = OutsourcePoStatus.Confirmed, PlantId = Plant, CreatedByRoleId = "1",
            });

            AddReceipt(db, "OPR-R1", new DateOnly(2099, 7, 5),  DocStatus.Posted,    qty: 100m, fee: 5000m, penalty: 250m);
            AddReceipt(db, "OPR-R2", new DateOnly(2099, 7, 15), DocStatus.Confirmed, qty: 80m,  fee: 4000m, penalty: 0m);
            AddReceipt(db, "OPR-R3", new DateOnly(2099, 7, 8),  DocStatus.Draft,     qty: 999m, fee: 9999m, penalty: 999m);

            db.SaveChanges();
        }

        private static void AddReceipt(ApplicationDbContext db, string id, DateOnly date, DocStatus status,
            decimal qty, decimal fee, decimal penalty)
        {
            var rec = new OutsourceReceipt
            {
                OutsourceReceiptId = id, OutsourcePurchaseOrderId = Opo, SupplierCode = Supplier,
                ReceiptDate = date, Status = status, CreatedBy = "tester",
                PlantId = Plant, CreatedByRoleId = "1",
            };
            rec.Lines.Add(new OutsourceReceiptLine
            {
                OutsourceReceiptId = id, LineNo = 1, ProductCode = "OPR-FG",
                ReceiptQty = qty, StockQty = qty, LineAmount = fee, ExcessScrapPenalty = penalty,
                UomCode = "PCS", StockUomCode = "PCS",
            });
            db.OutsourceReceipts.Add(rec);
        }

        private static void Purge(ApplicationDbContext db)
        {
            var recs = db.OutsourceReceipts.IgnoreQueryFilters().Include(r => r.Lines)
                .Where(r => r.PlantId == Plant).ToList();
            if (recs.Count > 0) db.OutsourceReceipts.RemoveRange(recs);
            var opos = db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ToList();
            if (opos.Count > 0) db.OutsourcePurchaseOrders.RemoveRange(opos);
            var cos = db.Companies.IgnoreQueryFilters().Where(c => c.CompanyCode == Supplier).ToList();
            if (cos.Count > 0) db.Companies.RemoveRange(cos);
            db.SaveChanges();
        }
    }
}
