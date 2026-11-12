using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.ACT
{
    /// <summary>
    /// 資料自癒：採購明細已收數量偏離來源進貨單時，預覽列得出來、核可後重新計算寫回、留下稽核紀錄。
    /// 走真 PostgreSQL：套用時要 <c>SELECT … FOR UPDATE</c> 鎖住目標明細（InMemory 不支援），
    /// 稽核紀錄的前後值與鍵值也要真的寫進 AuditLogs。
    /// </summary>
    [Collection("Integration")]
    public class DerivedFieldRepairIntegrationTests
    {
        private const string Plant = "ZZREPAIR";
        private const string OtherPlant = "ZZREPAIR2";
        private readonly TestWebApplicationFactory _factory;
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        public DerivedFieldRepairIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

        // 單號欄位是 varchar(30)：廠別取尾碼區分、本輪尾碼 8 碼
        private string Po(string plant) => $"RPR-PO{plant[^1..]}-{_run}";

        [Fact]
        public async Task 已收數量偏離_預覽列出_核可後依來源單據重算_留下稽核_再預覽就沒了()
        {
            await SeedAsync(Plant, recordedReceived: 10, grQty: 7, voidGrQty: 5);   // 應為 7（作廢那張不算），帳上記成 10

            using (var scope = _factory.Services.CreateScope())
            {
                var svc = Svc(scope);
                var p = Assert.Single(await svc.PreviewAsync(Plant), x => x.DocId == Po(Plant));
                Assert.Equal((DerivedFieldRepairService.PoReceivedQty, 1, 10m, 7m, 3m), (p.RepairType, p.LineNo, p.Current, p.Expected, p.Difference));

                var r = await svc.ApplyAsync(Plant, new[] { p.Key });
                Assert.Equal((1, 0, 0), (r.Applied, r.Unchanged, r.NotFound));
            }

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(7m, (await db.PurchaseOrderLines.IgnoreQueryFilters().SingleAsync(l => l.PurchaseOrderId == Po(Plant))).ReceivedQty);

                // 稽核：前後值都在。鍵值在 Core 稽核修正（ece9822，複合主鍵串接）同步後是「單號|行號」，之前只有單號——兩種都認
                var log = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.EntityType == nameof(PurchaseOrderLine) && a.Action == AuditAction.Modified
                             && (a.EntityId == $"{Po(Plant)}|1" || a.EntityId == Po(Plant)))
                    .OrderByDescending(a => a.Timestamp).FirstAsync();
                Assert.Contains("\"ReceivedQty\":10", log.Changes);
                Assert.Contains("\"ReceivedQty\":7", log.Changes);

                Assert.DoesNotContain(await Svc(scope).PreviewAsync(Plant), x => x.DocId == Po(Plant));
            }
        }

        [Fact]
        public async Task 預覽之後又進了貨_套用時以當下重算的值為準_不採信預覽的數字()
        {
            await SeedAsync(Plant, recordedReceived: 10, grQty: 7, voidGrQty: 0);
            string key;
            using (var scope = _factory.Services.CreateScope())
                key = Assert.Single(await Svc(scope).PreviewAsync(Plant), x => x.DocId == Po(Plant)).Key;   // 預覽時應為 7

            await AddGrAsync(Plant, qty: 2, voided: false);                                             // 核可前又進了 2

            using (var scope = _factory.Services.CreateScope())
            {
                await Svc(scope).ApplyAsync(Plant, new[] { key });
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(9m, (await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.PurchaseOrderId == Po(Plant))).ReceivedQty);
            }
        }

        [Fact]
        public async Task 只改目前廠別的單據_別廠的鍵送進來也不動()
        {
            await SeedAsync(OtherPlant, recordedReceived: 10, grQty: 7, voidGrQty: 0);
            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).ApplyAsync(Plant, new[] { $"{DerivedFieldRepairService.PoReceivedQty}:{Po(OtherPlant)}#1" });

            Assert.Equal((0, 1), (r.Applied, r.NotFound));
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(10m, (await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.PurchaseOrderId == Po(OtherPlant))).ReceivedQty);
        }

        [Fact]
        public async Task 不認得的鍵與空清單什麼都不做()
        {
            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).ApplyAsync(Plant, new[] { "DROP:TABLE#1", "PO_RECEIVED_QTY:no-line-number", "" });
            Assert.Equal((0, 0, 0), (r.Applied, r.Unchanged, r.NotFound));
        }

        private static IDerivedFieldRepairService Svc(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDerivedFieldRepairService>();

        private async Task SeedAsync(string plant, decimal recordedReceived, decimal grQty, decimal voidGrQty)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await db.Plants.AnyAsync(p => p.Id == plant))
                db.Plants.Add(new Plant { Id = plant, Name = $"{plant} 自癒測試", DefaultTimeZone = "Asia/Taipei" });
            var po = new PurchaseOrder
            {
                PurchaseOrderId = Po(plant), OrderDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedBy = "admin",
                SupplierCode = "RPR-SUP", ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = plant, CreatedByRoleId = "1",
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = Po(plant), LineNo = 1, ProductCode = "RPR-MAT", OrderQty = 100, UomCode = "EA",
                UnitPrice = 1, ReceivedQty = recordedReceived,
            });
            db.PurchaseOrders.Add(po);
            await db.SaveChangesAsync();
            await AddGrAsync(plant, grQty, voided: false);
            if (voidGrQty > 0) await AddGrAsync(plant, voidGrQty, voided: true);
        }

        private async Task AddGrAsync(string plant, decimal qty, bool voided)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var id = $"RPRGR{plant[^1..]}{_run}{Guid.NewGuid():N}"[..30];
            var gr = new GoodsReceipt
            {
                GoodsReceiptId = id, ReceiptDate = DateOnly.FromDateTime(DateTime.UtcNow), SupplierCode = "RPR-SUP",
                Status = voided ? DocStatus.Void : DocStatus.Confirmed, PlantId = plant, CreatedBy = "admin", CreatedByRoleId = "1",
            };
            gr.Lines.Add(new GoodsReceiptLine
            {
                GoodsReceiptId = id, LineNo = 1, PurchaseOrderId = Po(plant), PoLineNo = 1, ProductCode = "RPR-MAT",
                ReceiptQty = qty, StockQty = qty, UomCode = "EA", StockUomCode = "EA", UnitPrice = 1,
            });
            db.GoodsReceipts.Add(gr);
            await db.SaveChangesAsync();
        }
    }
}
