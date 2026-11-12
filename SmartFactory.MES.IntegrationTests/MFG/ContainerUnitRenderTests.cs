using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 箱子畫面（container-tracking.md §9）的伺服器渲染守門：清單狀態由庫存帳推導（不是存起來的旗標）、
    /// 掃箱號查得到、內容面板列出綁定批號的儲位、固定標籤 QR＝箱號。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ContainerUnitRenderTests
    {
        private const string Plant   = "P1";
        private const string Type    = "CUR-T";
        private const string Product = "CUR-FG";
        private const string Lot     = "CUR-LOT-01";
        private const string Prefix  = "CURX";
        private const string WipWo   = "CUR-WIP-WO";
        private const string WipPb   = "CUR-WIP-PB";

        private readonly AuthedScheduleFactory _factory;
        private readonly string _full;    // 裝著貨
        private readonly string _empty;   // 空箱
        private readonly string _wip;     // 在製（報工裝箱，§9.4）
        private readonly string _out;     // 廠外（批號跟著出貨單出完）
        private const string OutLot = "CUR-LOT-OUT";
        private const string OutDoc = "CUR-GS-01";

        public ContainerUnitRenderTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId.StartsWith(Prefix)).ExecuteDelete();
            db.ContainerUnits.IgnoreQueryFilters().Where(u => u.ContainerUnitId.StartsWith(Prefix)).ExecuteDelete();
            db.Inventories.IgnoreQueryFilters().Where(i => i.ProductCode == Product).ExecuteDelete();
            db.StockMovements.IgnoreQueryFilters().Where(m => m.ProductCode == Product).ExecuteDelete();
            db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == WipWo).ExecuteDelete();
            db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == WipPb).ExecuteDelete();
            db.GoodsShipments.IgnoreQueryFilters().Where(g => g.GoodsShipmentId == OutDoc).ExecuteDelete();
            if (!db.Containers.Any(c => c.ContainerCode == Type))
                db.Containers.Add(new Container { ContainerCode = Type, ContainerName = "渲染箱種", MaxCapacity = 10m });
            if (!db.Products.IgnoreQueryFilters().Any(p => p.ProductCode == Product))
                db.Products.Add(new Product { ProductCode = Product, ProductName = "箱子渲染成品", StockUom = "PC" });
            db.SaveChanges();

            // 走服務建號：同時驗證箱號格式（前綴-五位流水號）
            var ids = scope.ServiceProvider.GetRequiredService<IContainerUnitService>()
                .CreateBatchAsync(Type, Prefix, 4, "test").GetAwaiter().GetResult();
            _full = ids[0]; _empty = ids[1]; _wip = ids[2]; _out = ids[3];

            db.ChangeTracker.Clear();
            var u = db.ContainerUnits.IgnoreQueryFilters().Single(x => x.ContainerUnitId == _full);
            u.PlantId = Plant; u.ProductCode = Product; u.LotNo = Lot; u.BoundDocNo = "CUR-WO";
            db.ContainerUnits.IgnoreQueryFilters().Single(x => x.ContainerUnitId == _empty).PlantId = Plant;
            db.Inventories.Add(new Inventory
                { PlantId = Plant, ProductCode = Product, LocationCode = "CUR-LOC", LotNo = Lot, QtyOnHand = 7m });

            db.ProductionBatches.Add(new ProductionBatch
                { BatchId = WipPb, MrpRunId = "CUR-MRP", MrpRunLineSeq = 1, ProductCode = Product, Quantity = 10m, PlantId = Plant, CreatedByRoleId = "1" });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = WipWo, ProductionBatchId = WipPb, ProductCode = Product, Quantity = 10m,
                Status = WorkOrderStatus.InProgress, PlantId = Plant, CreatedByRoleId = "1",
            });
            var w = db.ContainerUnits.IgnoreQueryFilters().Single(x => x.ContainerUnitId == _wip);
            w.PlantId = Plant; w.ProductCode = Product; w.WorkOrderId = WipWo; w.OperationSeq = 20; w.BoundAt = DateTime.UtcNow;
            var o = db.ContainerUnits.IgnoreQueryFilters().Single(x => x.ContainerUnitId == _out);
            o.PlantId = Plant; o.ProductCode = Product; o.LotNo = OutLot; o.BoundAt = DateTime.UtcNow;
            db.ContainerUnitHistories.Add(new ContainerUnitHistory
                { ContainerUnitId = _out, PlantId = Plant, Action = ContainerUnitActions.Bind, ProductCode = Product, LotNo = OutLot, Qty = 3m });
            db.GoodsShipments.Add(new SmartFactory.MES.Modules.ORD.Models.GoodsShipment
            {
                GoodsShipmentId = OutDoc, ShipDate = new DateOnly(2097, 9, 1), CompanyCode = "CUR-CO",
                Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "test", PlantId = Plant, CreatedByRoleId = "1",
            });
            db.StockMovements.Add(new StockMovement
            {
                TypeCode = "GS", TypeCategory = "Sales", DocNo = OutDoc, DocLineNo = 1, PlantId = Plant,
                ProductCode = Product, LocationCode = "CUR-LOC", LotNo = OutLot, UomCode = "PC", StockUomCode = "PC",
                Qty = 3m, StockQty = 3m, Direction = -1, MovementDate = new DateOnly(2097, 9, 1),
            });
            db.SaveChanges();
        }

        [Fact]
        public void 箱號格式_前綴加五位流水號()
        {
            Assert.Matches($"^{Prefix}-\\d{{5}}$", _full);
        }

        [Fact]
        public async Task 清單_掃箱號查得到_狀態由帳推導()
        {
            var html = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}"));

            Assert.Contains(_full, html);
            Assert.Contains(_empty, html);
            Assert.Contains(Lot, html);                         // 裝著貨的列出批號
            Assert.Contains("使用中", html);
            Assert.Contains("空箱", html);

            var emptyOnly = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}&status=empty"));
            Assert.Contains(_empty, emptyOnly);
            Assert.DoesNotContain(_full, emptyOnly);
        }

        [Fact]
        public async Task 清單_在製箱由工單狀態推導_不算空箱()
        {
            var wipOnly = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}&status=wip"));
            Assert.Contains(_wip, wipOnly);
            Assert.DoesNotContain(_full, wipOnly);
            Assert.DoesNotContain(_empty, wipOnly);
            Assert.Contains($"{WipWo} / 20", wipOnly);
            Assert.Contains("在製", wipOnly);

            var emptyOnly = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}&status=empty"));
            Assert.DoesNotContain(_wip, emptyOnly);

            var panel = WebUtility.HtmlDecode(await _factory.CreateClient().GetStringAsync($"/ContainerUnit/Details/{_wip}"));
            Assert.Contains(WipWo, panel);
            Assert.Contains("在製", panel);
        }

        [Fact]
        public async Task 清單_廠外箱由出庫異動推導_不算空箱_面板顯示出廠單號()
        {
            var offOnly = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}&status=offsite"));
            Assert.Contains(_out, offOnly);
            Assert.Contains(OutDoc, offOnly);
            Assert.DoesNotContain(_empty, offOnly);
            Assert.DoesNotContain(_full, offOnly);

            var emptyOnly = WebUtility.HtmlDecode(await _factory.CreateClient()
                .GetStringAsync($"/ContainerUnit/Index?searched=true&searchTerm={Prefix}&status=empty"));
            Assert.DoesNotContain(_out, emptyOnly);

            var panel = WebUtility.HtmlDecode(await _factory.CreateClient().GetStringAsync($"/ContainerUnit/Details/{_out}"));
            Assert.Contains(OutDoc, panel);
            Assert.Contains("訂單出貨", panel);
            Assert.Contains("/ContainerUnit/Recover", panel);
        }

        [Fact]
        public async Task 出貨單面板與列印_列出跟著出廠的箱號()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.GetAsync($"/GoodsShipment/Details/{OutDoc}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var panel = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
            Assert.Contains("units-left", panel);
            Assert.Contains(_out, panel);
            Assert.Contains(OutLot, panel);
            Assert.DoesNotContain(_full, panel);

            var print = await _factory.CreateClient().GetAsync($"/GoodsShipment/Print/{OutDoc}");
            Assert.Equal(HttpStatusCode.OK, print.StatusCode);
            Assert.Contains(_out, await print.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task 內容面板_列出綁定批號的儲位與數量()
        {
            var resp = await _factory.CreateClient().GetAsync($"/ContainerUnit/Details/{_full}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
            Assert.Contains(Lot, html);
            Assert.Contains("CUR-LOC", html);
            Assert.Contains("CUR-WO", html);
        }

        [Fact]
        public async Task 固定標籤_QR等於箱號_不印內容()
        {
            var resp = await _factory.CreateClient().GetAsync($"/ContainerUnit/Labels?ids={_full},{_empty}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"box-label\"").Count);
            Assert.Contains(_full, html);
            Assert.DoesNotContain(Lot, html);                    // 箱子重複使用，標籤不印內容
        }

        [Fact]
        public async Task 掃箱號API_使用中回批號與儲位_空箱回404()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync($"/api/warehouse/container-unit/{_full}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(Lot, doc.RootElement.GetProperty("lotNo").GetString());
            Assert.Equal(Product, doc.RootElement.GetProperty("productCode").GetString());
            var loc = Assert.Single(doc.RootElement.GetProperty("locations").EnumerateArray().ToList());
            Assert.Equal("CUR-LOC", loc.GetProperty("locationCode").GetString());
            Assert.Equal(7m, loc.GetProperty("qty").GetDecimal());

            // 空箱、不是箱號：404，畫面照原樣把輸入當批號
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/warehouse/container-unit/{_empty}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/warehouse/container-unit/NOT-A-BOX")).StatusCode);
        }
    }
}
