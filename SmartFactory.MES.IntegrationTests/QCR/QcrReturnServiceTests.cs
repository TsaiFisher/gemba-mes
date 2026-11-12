using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.QCR.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QCR
{
    /// <summary>
    /// NCR 處置=Return → 自動建立採購退貨草稿單（QCR 成本勾稽）端到端驗證。
    ///
    /// 廠別 ZZQRT：進貨單 QRT-GR（供應商 QRT-SUP，明細單價 40）→ IQC 檢驗單（拒批）→ NCR（Return, 數量 25）。
    /// 期望：`CreateDraftReturnFromNcrAsync` 建立 Draft GoodsReturn，帶入供應商、連結來源進貨單、
    ///       單價 40、金額 1000（=25×40）、Remarks="NCR {id}"。
    /// 另驗：非 IQC 來源（SourceType=Manual）擲 InvalidOperationException。
    /// 真過帳/單號序列僅 PG 具備，故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class QcrReturnServiceTests
    {
        private const string Plant    = "ZZQRT";
        private const string Supplier = "QRT-SUP";
        private const string Receipt  = "QRT-GR";
        private const string Product  = "QRT-P";
        private const string IqcOrder = "QRT-IQC1";
        private const string NcrId    = "QRT-NCR1";
        private const string NcrManual = "QRT-NCR2";

        private readonly TestWebApplicationFactory _factory;

        public QcrReturnServiceTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task CreateDraftReturnFromNcr_IqcSource_CreatesLinkedDraftReturn()
        {
            using var scope = _factory.Services.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IQcrReturnService>();

            var ncr = await db.NonConformanceReports.IgnoreQueryFilters().FirstAsync(n => n.NcrId == NcrId);

            var returnId = await svc.CreateDraftReturnFromNcrAsync(ncr, Plant, "tester");

            Assert.False(string.IsNullOrWhiteSpace(returnId));

            var ret = await db.GoodsReturns.IgnoreQueryFilters()
                .Include(r => r.Lines).FirstAsync(r => r.GoodsReturnId == returnId);
            Assert.Equal(DocStatus.Draft, ret.Status);
            Assert.Equal(Supplier, ret.SupplierCode);
            Assert.Equal(1000m, ret.TotalAmount);          // 25 × 40
            Assert.Equal($"NCR {NcrId}", ret.Remarks);

            // 雙向連結：NCR 回填退貨單號
            var ncrAfter = await db.NonConformanceReports.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(n => n.NcrId == NcrId);
            Assert.Equal(returnId, ncrAfter.GoodsReturnId);

            var line = Assert.Single(ret.Lines);
            Assert.Equal(Receipt, line.GoodsReceiptId);     // 連結來源進貨單
            Assert.Equal(1, line.ReceiptLineNo);
            Assert.Equal(Product, line.ProductCode);
            Assert.Equal(25m, line.ReturnQty);
            Assert.Equal(40m, line.UnitPrice);              // 取自進貨明細
            Assert.Equal(1000m, line.LineAmount);
        }

        [Fact]
        public async Task CreateDraftReturnFromNcr_NonIqcSource_Throws()
        {
            using var scope = _factory.Services.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IQcrReturnService>();

            var ncr = await db.NonConformanceReports.IgnoreQueryFilters().FirstAsync(n => n.NcrId == NcrManual);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CreateDraftReturnFromNcrAsync(ncr, Plant, "tester"));
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Purge(db);

            if (!db.DocNumberConfigs.Any(c => c.DocType == "GoodsReturn"))
                db.DocNumberConfigs.Add(new DocNumberConfig
                {
                    DocType = "GoodsReturn", DocTypeName = "採購退貨單", Prefix = "PF",
                    SeqLength = 5, IsActive = true,
                });

            db.Companies.Add(new SmartFactory.MES.Modules.BAS.Models.Company
            {
                CompanyCode = Supplier, CompanyName = "退貨供應商", IsSupplier = true, IsActive = true,
            });

            var gr = new GoodsReceipt
            {
                GoodsReceiptId = Receipt, ReceiptDate = new DateOnly(2099, 8, 1),
                SupplierCode = Supplier, CreatedBy = "tester", Status = DocStatus.Confirmed,
                PlantId = Plant, CreatedByRoleId = "1",
            };
            gr.Lines.Add(new GoodsReceiptLine
            {
                GoodsReceiptId = Receipt, LineNo = 1, ProductCode = Product,
                UnitPrice = 40m, UomCode = "PCS", StockUomCode = "PCS",
                WhCode = "QRT-WH", LocationCode = "QRT-LOC", LotNo = "QRT-LOT",
            });
            db.GoodsReceipts.Add(gr);

            db.InspectionOrders.Add(new InspectionOrder
            {
                InspectionOrderId = IqcOrder, InspectionType = "IQC",
                SourceDocId = Receipt, SourceLineNo = 1, ProductCode = Product,
                LotSize = 100m, Status = "Fail", PlantId = Plant,
            });

            db.NonConformanceReports.Add(new NonConformanceReport
            {
                NcrId = NcrId, SourceType = "InspectionOrder", SourceId = IqcOrder,
                ProductCode = Product, Qty = 25m, UomCode = "PCS",
                WhCode = "QRT-WH", LocationCode = "QRT-LOC", LotNo = "QRT-LOT",
                Disposition = "Return", NcrStatus = NcrStatus.Open,
                CreatedBy = "tester", PlantId = Plant, CreatedByRoleId = "1",
            });

            // 非 IQC 來源（手動）：退貨不適用
            db.NonConformanceReports.Add(new NonConformanceReport
            {
                NcrId = NcrManual, SourceType = "Manual",
                ProductCode = Product, Qty = 5m, Disposition = "Return", NcrStatus = NcrStatus.Open,
                CreatedBy = "tester", PlantId = Plant, CreatedByRoleId = "1",
            });

            db.SaveChanges();
        }

        private static void Purge(ApplicationDbContext db)
        {
            var rets = db.GoodsReturns.IgnoreQueryFilters().Include(r => r.Lines)
                .Where(r => r.PlantId == Plant).ToList();
            if (rets.Count > 0) db.GoodsReturns.RemoveRange(rets);
            var ncrs = db.NonConformanceReports.IgnoreQueryFilters().Where(n => n.PlantId == Plant).ToList();
            if (ncrs.Count > 0) db.NonConformanceReports.RemoveRange(ncrs);
            var orders = db.InspectionOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ToList();
            if (orders.Count > 0) db.InspectionOrders.RemoveRange(orders);
            var grs = db.GoodsReceipts.IgnoreQueryFilters().Include(g => g.Lines).Where(g => g.PlantId == Plant).ToList();
            if (grs.Count > 0) db.GoodsReceipts.RemoveRange(grs);
            var cos = db.Companies.IgnoreQueryFilters().Where(c => c.CompanyCode == Supplier).ToList();
            if (cos.Count > 0) db.Companies.RemoveRange(cos);
            db.SaveChanges();
        }
    }
}
