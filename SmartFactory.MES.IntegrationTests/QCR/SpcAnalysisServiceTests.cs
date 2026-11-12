using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.QCR.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QCR
{
    /// <summary>
    /// SpcAnalysisService 端到端：撈檢驗計量值 → 依（品號×特性）算 Cp/Cpk/超規 → 附管制圖。
    /// 廠別 ZZSPC，品號 SPC-P 的特性 Dim-A 三筆量測值 {2,4,6}（LSL=-2, USL=10）：
    ///   mean=4、σ=2 → Cp=(10−(−2))/(6·2)=1、Cpk=min(Cpu,Cpl)=1、超規=0。
    /// </summary>
    [Collection("Integration")]
    public class SpcAnalysisServiceTests
    {
        private const string Plant   = "ZZSPC";
        private const string Product = "SPC-P";
        private const string Item    = "Dim-A";

        private readonly TestWebApplicationFactory _factory;

        public SpcAnalysisServiceTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task Build_computes_capability_per_characteristic()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISpcAnalysisService>();

            var rpt = await svc.BuildAsync(
                Plant, null, new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31));

            var row = Assert.Single(rpt.Rows);
            Assert.Equal(Product, row.ProductCode);
            Assert.Equal(Item, row.ItemName);
            Assert.Equal(3, row.SampleCount);
            Assert.Equal(4m, row.Mean);
            Assert.Equal(2m, row.StdDev);
            Assert.Equal(1.00m, row.Cp);
            Assert.Equal(1.00m, row.Cpk);
            Assert.Equal(0, row.OutOfSpecCount);
            Assert.False(rpt.HasChart);   // 未鎖定品號 → 無管制圖
        }

        [Fact]
        public async Task Build_with_product_filter_populates_control_chart()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISpcAnalysisService>();

            var rpt = await svc.BuildAsync(
                Plant, Product, new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31));

            Assert.True(rpt.HasChart);
            var series = Assert.Single(rpt.Charts);
            Assert.Equal(Item, series.ItemName);
            Assert.Equal(3, series.Values.Count);       // 三個時間序點
            Assert.Equal(new[] { 2m, 4m, 6m }, series.Values);  // 依 InspectedAt 排序
            Assert.Equal(10m, series.Ucl);              // mean+3σ
            Assert.Equal(-2m, series.Lcl);              // mean−3σ
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Purge(db);

            db.Products.Add(new Product
            {
                ProductCode = Product, ProductName = "SPC 測試品", IsActive = true,
            });

            // 三筆量測值 2/4/6（時間序遞增），LSL=-2 USL=10 TARGET=4
            var samples = new[]
            {
                (Ord: "SPC-O1", Val: 2m, At: new DateTime(2099, 3, 1, 8, 0, 0, DateTimeKind.Utc)),
                (Ord: "SPC-O2", Val: 4m, At: new DateTime(2099, 3, 2, 8, 0, 0, DateTimeKind.Utc)),
                (Ord: "SPC-O3", Val: 6m, At: new DateTime(2099, 3, 3, 8, 0, 0, DateTimeKind.Utc)),
            };

            foreach (var s in samples)
            {
                db.InspectionOrders.Add(new InspectionOrder
                {
                    InspectionOrderId = s.Ord, PlantId = Plant, InspectionType = "IPQC",
                    SourceDocId = "SPC-DISP", ProductCode = Product, LotSize = 100m,
                    Status = "Pass", InspectedAt = s.At,
                });
                db.InspectionItems.Add(new InspectionItem
                {
                    ItemId = s.Ord + "-I1", InspectionOrderId = s.Ord, SeqNo = 1,
                    ItemName = Item, UomCode = "mm",
                    LowerSpec = -2m, TargetValue = 4m, UpperSpec = 10m,
                    MeasuredValue = s.Val, IsConforming = true,
                });
            }

            db.SaveChanges();
        }

        private static void Purge(ApplicationDbContext db)
        {
            var items = db.InspectionItems.IgnoreQueryFilters()
                .Where(i => i.InspectionOrderId.StartsWith("SPC-O")).ToList();
            if (items.Count > 0) db.InspectionItems.RemoveRange(items);
            var orders = db.InspectionOrders.IgnoreQueryFilters()
                .Where(o => o.PlantId == Plant).ToList();
            if (orders.Count > 0) db.InspectionOrders.RemoveRange(orders);
            var prods = db.Products.IgnoreQueryFilters()
                .Where(p => p.ProductCode == Product).ToList();
            if (prods.Count > 0) db.Products.RemoveRange(prods);
            db.SaveChanges();
        }
    }
}
