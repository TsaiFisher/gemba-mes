using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using BAS = SmartFactory.MES.Modules.BAS.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 完工入庫分箱（container-tracking.md §8 L1+）的伺服器渲染守門：確認面板帶得出預設容器與每箱數量、
    /// 箱標籤頁每個批號一張且作廢回沖掉的批號不再印。帳務正確性由 Flows/MfgCompletionBoxingFlowTests 負責。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ProductionCompletionBoxingRenderTests
    {
        private const string Plant     = "P1";
        private const string RoleId    = "1";
        private const string Product   = "BOXR-FG";
        private const string Routing   = "BOXR-RT";
        private const string Container = "BOXR-CT";
        private const string Wo        = "BOXR-WO";
        private const string Batch     = "BOXR-PB";
        private const string OtherContainer = "BOXR-OT";

        private readonly AuthedScheduleFactory _factory;

        public ProductionCompletionBoxingRenderTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == Wo).ExecuteDelete();
            db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDelete();
            db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDelete();
            db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDelete();
            db.RoutingOperations.Where(o => o.RoutingId == Routing).ExecuteDelete();
            db.Containers.Where(c => c.ContainerCode == Container || c.ContainerCode == OtherContainer).ExecuteDelete();

            if (!db.Products.IgnoreQueryFilters().Any(p => p.ProductCode == Product))
                db.Products.Add(new BAS.Product { ProductCode = Product, ProductName = "分箱渲染成品", StockUom = "PC" });
            db.Containers.Add(new BAS.Container
                { ContainerCode = Container, ContainerName = "渲染週轉箱", MaxCapacity = 25m, IsActive = true });
            db.Containers.Add(new BAS.Container
                { ContainerCode = OtherContainer, ContainerName = "渲染棧板", MaxCapacity = 80m, IsActive = true });
            // 途程只要最後一道工序的完工容器；第 10 道刻意設別的，確認取的是「最後一道」
            db.RoutingOperations.Add(new MfgM.RoutingOperation
                { RoutingId = Routing, OperationSeq = 10, WorkCenterId = "BOXR-WC", ContainerOutCode = OtherContainer });
            db.RoutingOperations.Add(new MfgM.RoutingOperation
                { RoutingId = Routing, OperationSeq = 20, WorkCenterId = "BOXR-WC", ContainerOutCode = Container });
            db.ProductionBatches.Add(new MfgM.ProductionBatch
            {
                BatchId = Batch, MrpRunId = "BOXR-MRP", MrpRunLineSeq = 1,
                ProductCode = Product, Quantity = 60m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new MfgM.WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Product, RoutingId = Routing,
                Quantity = 60m, Status = MfgM.WorkOrderStatus.Completed, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            foreach (var seq in new[] { 10, 20 })
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                    { WorkOrderId = Wo, OperationSeq = seq, WorkCenterId = "BOXR-WC", UnitTime = 1m });

            // 三箱入庫，其中第 3 箱被作廢回沖（淨額 0）→ 標籤頁只印兩張
            var day = new DateOnly(2097, 7, 1);
            void Move(string lot, decimal qty, int dir) => db.StockMovements.Add(new StockMovement
            {
                TypeCode = "IT", TypeCategory = "Production", DocNo = Wo, DocLineNo = 1, MovementDate = day,
                PlantId = Plant, ProductCode = Product, LocationCode = "BOXR-LOC", LotNo = lot,
                UomCode = "PC", Qty = qty, StockUomCode = "PC", StockQty = qty, Direction = dir, UnitCost = 1m,
            });
            Move($"{Wo}-01", 25m, 1);
            Move($"{Wo}-02", 25m, 1);
            Move($"{Wo}-03", 10m, 1);
            Move($"{Wo}-03", 10m, -1);
            db.SaveChanges();
        }

        [Fact]
        public async Task ConfirmForm_預設容器取末道工序_每箱數量取容器容量()
        {
            var html = await _factory.CreateClient().GetStringAsync($"/ProductionCompletion/ConfirmForm/{Wo}");

            Assert.Contains("id=\"packByBox\"", html);
            Assert.Matches($"<option value=\"{Container}\"[^>]*selected", html);
            Assert.Contains("id=\"boxQty\"", html);
            Assert.Matches("id=\"boxQty\"[^>]*value=\"25", html);
            Assert.Contains("data-template=", html);
            Assert.DoesNotContain("data-template=\"\"", html);
        }

        [Fact]
        public async Task BoxLabels_每個淨額為正的批號一張_作廢回沖的不印()
        {
            var resp = await _factory.CreateClient().GetAsync($"/ProductionCompletion/BoxLabels/{Wo}?containerCode={Container}");
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();

            Assert.Contains($"{Wo}-01", html);
            Assert.Contains($"{Wo}-02", html);
            Assert.DoesNotContain($"{Wo}-03", html);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"box-label\"").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<svg").Count);
            Assert.Contains("box-label.css", html);
            Assert.Contains("渲染週轉箱", System.Net.WebUtility.HtmlDecode(html));   // 中文經 HtmlEncoder 輸出為 &#x...;
        }

        [Fact]
        public async Task BoxLabels_沒有入庫異動_回404()
        {
            var resp = await _factory.CreateClient().GetAsync("/ProductionCompletion/BoxLabels/NO-SUCH-WO");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        }
    }
}
