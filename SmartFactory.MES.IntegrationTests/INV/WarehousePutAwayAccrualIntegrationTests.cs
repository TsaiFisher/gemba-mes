using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.INV
{
    /// <summary>
    /// put-away 認列/回沖「端到端真 PG」整合驗證。
    ///
    /// 既有 <c>WarehouseApiControllerTests</c>（單元/InMemory）只以 mock IAP/IARInvoiceService + Verify
    /// 釘住 controller 的接線；本測試補上真 PostgreSQL、真 DI（WarehouseApiController → 真
    /// InventoryPostingService → 真 APInvoiceService/ARInvoiceService → 真 JournalEntryService）的完整
    /// 鏈路：驗證免驗品上架正常倉時，實際在 PG 產生一張 APInvoice 明細 + 一組 GL 分錄
    /// （借 2152 淨額 / 借 1155 進項稅額 / 貸 2100 含稅），以及銷退上架時實際回沖來源出貨 AR 明細（不入 GL）。
    ///
    /// 隔離：專屬廠別 ZZWAP + 遠期日（2088-08，其他整合測都群聚在 2099、未觸及 2088）避開會計期間鎖
    /// 與他測殘留；每次重跑先 Purge（含刪掉 2088-08/09 期間，確保「期間不存在→無鎖→放行」前提成立——
    /// 曾因用 2099 撞到他測建的 209908=Closing 期間而讓 put-away 過帳被擋回 BadRequest）。
    /// 真過帳/真單號序列/真 GL 僅 PG 具備，故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class WarehousePutAwayAccrualIntegrationTests : IDisposable
    {
        private const string Plant    = "ZZWAP";
        private const string RoleId   = "1";
        private const string Supplier = "WAP-SUP";
        private const string Customer = "WAP-CUS";
        private const string Category = "WAP-CAT";
        private const string ExemptProduct = "WAP-EX";      // IqcExempt=true → 免 IQC 直上正常倉
        private const string ReturnProduct = "WAP-RP";
        private const string Warehouse = "WAP-WH";          // WarehouseType.Raw（正常倉）
        private const string Location  = "WAP-LOC";
        private const string Gr        = "WAP-GR1";         // 進貨單（AP 認列）
        private const string ShipmentId = "WAP-GS1";        // 來源出貨（AR 回沖對象）
        private const string SalesReturnId = "WAP-SR1";     // 銷退單
        private const string ReturnProduct2  = "WAP-RP2";   // 售價與成本差五倍的銷退料
        private const string ShipmentId2    = "WAP-GS2";    // 有庫存異動的來源出貨（成本基礎）
        private const string SalesReturnId2 = "WAP-SR2";    // 銷退單（驗入庫成本取自出貨成本）

        private readonly TestWebApplicationFactory _factory;

        public WarehousePutAwayAccrualIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        /// <summary>
        /// 跑完再清一次。
        ///
        /// 原本只在建構子 Seed() 裡先 Purge（跑之前清），最後一輪的產物會一直留在共用
        /// 測試庫裡。其中 WAP-AR1 這張 AR 發票的 SourceDocId 指向 WAP-GS1，
        /// 而本測試刻意不建那張出貨單（回沖只需要發票明細與銷退單）——於是它是一張
        /// 貨真價實的孤兒發票，會被孤兒過帳稽核（已掛進 RunAllAsync）報出來。
        /// 那是測試殘留而不是產品缺陷，但兩者在稽核報表上長得一模一樣。
        /// </summary>
        public void Dispose()
        {
            using var scope = _factory.Services.CreateScope();
            Purge(Scoped(scope));
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task PutAway_ExemptProduct_ToNormalWarehouse_CreatesRealApInvoiceAndGlEntry()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            var ctrl = BuildController(scope, db);

            // 進貨 100、單價 300、稅率 5%；上架 100（全量）到正常倉。
            var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
            {
                TypeCode = "PI", DocNo = Gr, LineNo = 1,
                WhCode = Warehouse, LocationCode = Location, Qty = 100m, LotNo = "",
            });
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);

            using var v = _factory.Services.CreateScope();
            var vdb = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 真的產生一張 AP（來源=進貨單），明細認列量＝上架量 100。
            var ap = await vdb.APInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                .FirstAsync(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == Gr);
            Assert.Equal(100m, ap.Lines.First().Qty);
            Assert.Equal(30000m, ap.Lines.First().LineAmount);   // 100 × 300
            Assert.Equal(31500m, ap.TotalAmount);                // 含 5% 稅

            // 真的入 GL：借 2152 淨額 30000 / 借 1155 進項稅額 1500 / 貸 2100 含稅 31500，借貸平衡。
            var glLines = vdb.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .Where(e => e.SourceDocType == "APInvoice" && e.SourceDocId == ap.APInvoiceId)
                .SelectMany(e => e.Lines).ToList();
            Assert.Equal(30000m, glLines.Where(l => l.AccountCode == "2152").Sum(l => l.Debit));
            Assert.Equal(1500m,  glLines.Where(l => l.AccountCode == "1155").Sum(l => l.Debit));
            Assert.Equal(31500m, glLines.Where(l => l.AccountCode == "2100").Sum(l => l.Credit));
            Assert.Equal(glLines.Sum(l => l.Debit), glLines.Sum(l => l.Credit));

            // 庫存確有進倉（真過帳）。
            var inv = await vdb.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.ProductCode == ExemptProduct && i.LocationCode == Location);
            Assert.Equal(100m, inv.QtyOnHand);
        }

        [Fact]
        public async Task PutAwaySalesReturn_ToNormalWarehouse_ReversesRealArInvoiceLine()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            var ctrl = BuildController(scope, db);

            // 銷退上架 12（全量）→ 回沖來源出貨 AR 明細（原 20），剩 8。
            var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
            {
                TypeCode = "GR", DocNo = SalesReturnId, LineNo = 1,
                WhCode = Warehouse, LocationCode = Location, Qty = 12m, LotNo = "",
            });
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);

            using var v = _factory.Services.CreateScope();
            var vdb = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var ar = await vdb.ARInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                .FirstAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == ShipmentId);
            var arLine = ar.Lines.First(l => l.ProductCode == ReturnProduct);
            Assert.Equal(8m, arLine.Qty);            // 20 − 12
            Assert.Equal(160m, arLine.LineAmount);   // 8 × 20

            // AR 產生本身不入 GL（僅子帳），回沖亦不入 GL：不應有 ARInvoiceReturn 類分錄。
            var arReturnEntries = vdb.JournalEntries.IgnoreQueryFilters()
                .Count(e => e.SourceDocType == "ARInvoiceReturn" && e.SourceDocId == ShipmentId);
            Assert.Equal(0, arReturnEntries);
        }

        /// <summary>
        /// 銷退上架的入庫成本必須取**原出貨異動實際扣掉的單位成本**，不是銷退單上的售價。
        ///
        /// 銷退單畫面上沒有成本欄位，帶過來的 UnitPrice 就是售價。簡化模式早就改走
        /// <c>SalesReturnCostBasis</c>，完整模式的上架路徑卻一直沿用 <c>line.UnitPrice</c>——
        /// 同一張銷退單走哪個模式，入庫成本竟然是兩個數字。
        ///
        /// 售價（500）與成本（100）刻意差五倍：兩者相同的話，這支測試永遠是綠的。
        /// 壞掉時的均價是 (10×100 + 5×500)/15 = 233.33；修好是 100。
        /// 三張帳都不會叫——傳票借貸照樣平衡（只是把錯的數字寫進去），
        /// INV_ROLLFORWARD 只比數量而數量完全正確。
        /// </summary>
        [Fact]
        public async Task PutAwaySalesReturn_PricesAtSourceShipmentCost_NotSalesPrice()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            var ctrl = BuildController(scope, db);

            var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
            {
                TypeCode = "GR", DocNo = SalesReturnId2, LineNo = 1,
                WhCode = Warehouse, LocationCode = Location, Qty = 5m, LotNo = "",
            });
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);

            using var v = _factory.Services.CreateScope();
            var vdb = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var mv = await vdb.StockMovements.IgnoreQueryFilters()
                .SingleAsync(m => m.DocNo == SalesReturnId2 && m.Direction == 1);
            Assert.Equal(100m, mv.UnitCost);
            Assert.Equal(500m, mv.TotalCost);          // 5 × 100，不是 5 × 500

            // 移動平均不得被售價汙染：同價進出，均價原地不動
            var inv = await vdb.Inventories.IgnoreQueryFilters()
                .SingleAsync(i => i.PlantId == Plant && i.ProductCode == ReturnProduct2);
            Assert.Equal(15m, inv.QtyOnHand);
            Assert.Equal(100m, inv.UnitCost);

            // 存貨科目借方＝實際成本，與庫存明細帳同一個數字
            var invDebit = await vdb.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == SalesReturnId2)
                .SelectMany(e => e.Lines)
                .Where(l => l.AccountCode == StandardAccounts.RawMaterialInventoryCode)
                .SumAsync(l => (decimal?)l.Debit) ?? 0m;
            Assert.Equal(500m, invDebit);
        }

        // ─────────────────────── 基礎建設 ───────────────────────

        private ApplicationDbContext Scoped(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 全域過濾以 DbContext 欄位驅動：對齊種子的廠別與建立角色，controller 的「已過濾」讀取才看得到單據。
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return db;
        }

        private WarehouseApiController BuildController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = scope.ServiceProvider;
            // context 及以下服務皆來自同一 scope → 共用同一個 scoped ApplicationDbContext，
            // 上架過帳與後續 AP/AR 認列在同一交易語境中作用。
            return new WarehouseApiController(
                db,
                new FakeUser(Plant, RoleId),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(),
                new AllowAllPermission(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IAPInvoiceService>(),
                sp.GetRequiredService<IARInvoiceService>());
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            Purge(db);

            foreach (var (docType, name, prefix) in new[]
            {
                ("APInvoice", "應付發票", "AP"),
                ("JournalEntry", "日記帳", "JE"),
            })
            {
                if (!db.DocNumberConfigs.Any(c => c.DocType == docType))
                    db.DocNumberConfigs.Add(new DocNumberConfig
                    { DocType = docType, DocTypeName = name, Prefix = prefix, SeqLength = 6, IsActive = true });
            }

            if (!db.ProductCategories.Any(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                { CategoryCode = Category, CategoryName = "整合測試料", CostMethod = CostMethod.MovingAverage });

            db.Companies.Add(new Company { CompanyCode = Supplier, CompanyName = "整合供應商", IsSupplier = true, IsActive = true });
            db.Companies.Add(new Company { CompanyCode = Customer, CompanyName = "整合客戶", IsCustomer = true, IsActive = true });

            db.Products.Add(new Product
            { ProductCode = ExemptProduct, ProductName = "免驗料", StockUom = "PC", CategoryCode = Category, IqcExempt = true });
            db.Products.Add(new Product
            { ProductCode = ReturnProduct, ProductName = "銷退料", StockUom = "PC", CategoryCode = Category });
            db.Products.Add(new Product
            { ProductCode = ReturnProduct2, ProductName = "銷退料（成本基礎）", StockUom = "PC", CategoryCode = Category });

            db.Warehouses.Add(new Warehouse
            { WhCode = Warehouse, WhName = "整合正常倉", PlantId = Plant, WarehouseType = WarehouseType.Raw });
            db.StorageLocations.Add(new StorageLocation
            { WhCode = Warehouse, LocationCode = Location, LocationName = "整合儲位" });

            // ── AP 認列來源：進貨單（Confirmed+Approved），進貨 100、單價 300、稅率 5% ──
            var gr = new GoodsReceipt
            {
                GoodsReceiptId = Gr, ReceiptDate = new DateOnly(2088, 8, 15),
                SupplierCode = Supplier, CreatedBy = "tester",
                Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
                TypeCode = "PI", TypeCategory = "Receipt", PlantId = Plant, CreatedByRoleId = RoleId,
            };
            gr.Lines.Add(new GoodsReceiptLine
            {
                GoodsReceiptId = Gr, LineNo = 1, ProductCode = ExemptProduct,
                ReceiptQty = 100m, UomCode = "PC", StockQty = 100m, StockUomCode = "PC",
                UnitPrice = 300m, TaxRate = 0.05m, WhCode = Warehouse, LocationCode = Location,
            });
            db.GoodsReceipts.Add(gr);

            // ── AR 回沖來源：出貨已開立的 AR 發票（Open），明細品號 WAP-RP、Qty 20、單價 20 ──
            var ar = new ARInvoice
            {
                ARInvoiceId = "WAP-AR1", CustomerCode = Customer,
                InvoiceDate = new DateOnly(2088, 8, 10), DueDate = new DateOnly(2088, 9, 10),
                Status = ARInvoiceStatus.Open, ExchangeRate = 1m,
                SourceDocType = "GoodsShipment", SourceDocId = ShipmentId,
                TotalAmount = 400m, AmountLocal = 400m,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            ar.Lines.Add(new ARInvoiceLine
            {
                ARInvoiceId = "WAP-AR1", LineNo = 1, ProductCode = ReturnProduct,
                Qty = 20m, UomCode = "PC", UnitPrice = 20m, LineAmount = 400m,
                // 回沖以 SourceLineNo 對應來源出貨行號（GenerateFromShipmentAsync 開票時寫入）。
                // 此處手工建發票，必須自行對齊，否則回沖找不到明細而靜默 no-op。
                SourceLineNo = 1,
            });
            db.ARInvoices.Add(ar);

            // ── 銷退單（Confirmed+Approved），退回 12，連結來源出貨 ──
            var sr = new SalesReturn
            {
                SalesReturnId = SalesReturnId, ReturnDate = new DateOnly(2088, 8, 20),
                TypeCode = "GR", CompanyCode = Customer, GoodsShipmentId = ShipmentId,
                Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "tester", PlantId = Plant, CreatedByRoleId = RoleId,
            };
            sr.Lines.Add(new SalesReturnLine
            {
                SalesReturnId = SalesReturnId, LineNo = 1, GoodsShipmentId = ShipmentId, ShipmentLineNo = 1,
                ProductCode = ReturnProduct, ReturnQty = 12m, UomCode = "PC",
                StockQty = 12m, StockUomCode = "PC", UnitPrice = 20m,
            });
            db.SalesReturns.Add(sr);

            // ── 成本基礎樣本：期初 10 個 @ 100，來源出貨已扣掉 5 個（凍結單價 100）──
            db.Inventories.Add(new Inventory
            {
                PlantId = Plant, ProductCode = ReturnProduct2, LocationCode = Location, LotNo = "",
                QtyOnHand = 10m, QtyReserved = 0m, UnitCost = 100m, LastUpdatedAt = DateTime.UtcNow,
            });
            db.StockMovements.Add(new StockMovement
            {
                TypeCode = "GS", TypeCategory = "Sales", DocNo = ShipmentId2, DocLineNo = 1,
                MovementDate = new DateOnly(2088, 8, 18), PlantId = Plant,
                ProductCode = ReturnProduct2, LocationCode = Location, LotNo = "",
                UomCode = "PC", Qty = 5m, StockUomCode = "PC", StockQty = 5m,
                Direction = -1, UnitCost = 100m, TotalCost = 500m, CreatedBy = "tester",
            });

            // 銷退單：退 5，單價欄位是**售價** 500（成本 100 的五倍）
            var sr2 = new SalesReturn
            {
                SalesReturnId = SalesReturnId2, ReturnDate = new DateOnly(2088, 8, 20),
                TypeCode = "GR", CompanyCode = Customer, GoodsShipmentId = ShipmentId2,
                Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = "tester", PlantId = Plant, CreatedByRoleId = RoleId,
            };
            sr2.Lines.Add(new SalesReturnLine
            {
                SalesReturnId = SalesReturnId2, LineNo = 1, GoodsShipmentId = ShipmentId2, ShipmentLineNo = 1,
                ProductCode = ReturnProduct2, ReturnQty = 5m, UomCode = "PC",
                StockQty = 5m, StockUomCode = "PC", UnitPrice = 500m,
            });
            db.SalesReturns.Add(sr2);

            db.SaveChanges();
        }

        private static void Purge(ApplicationDbContext db)
        {
            // GL 分錄（連明細走級聯）— 避免重跑撞 PK
            // 廠別內全刪，不挑 SourceDocType——只刪 AP 那兩種的話，上架過帳寫的 INV 分錄
            // 會一輪一輪疊上去，「本單存貨借方合計」這種斷言第二次跑就變成兩倍，
            // 而且失敗訊息看起來像產品壞掉（實際上是測試自己沒清乾淨）。
            var jes = db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .Where(e => e.PlantId == Plant).ToList();
            if (jes.Count > 0) { db.JournalEntries.RemoveRange(jes); db.SaveChanges(); }

            var aps = db.APInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                .Where(i => i.SourceDocId == Gr).ToList();
            if (aps.Count > 0) db.APInvoices.RemoveRange(aps);
            var ars = db.ARInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                .Where(i => i.SourceDocId == ShipmentId).ToList();
            if (ars.Count > 0) db.ARInvoices.RemoveRange(ars);

            db.StockMovements.IgnoreQueryFilters().Where(m => m.PlantId == Plant).ExecuteDelete();
            db.Inventories.IgnoreQueryFilters().Where(i => i.PlantId == Plant).ExecuteDelete();

            // 會計期間為全域（無廠別）：刪掉本測試用到的 2088-08/09 期間，回到「該期間不存在→無鎖→
            // 一般過帳放行」的前提。他測若曾把這兩個月建成 Closing/Closed 會擋掉 put-away 過帳（BadRequest）。
            db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == "208808" || p.PeriodId == "208809").ExecuteDelete();

            var grs = db.GoodsReceipts.IgnoreQueryFilters().Include(g => g.Lines).Where(g => g.PlantId == Plant).ToList();
            if (grs.Count > 0) db.GoodsReceipts.RemoveRange(grs);
            var srs = db.SalesReturns.IgnoreQueryFilters().Include(s => s.Lines).Where(s => s.PlantId == Plant).ToList();
            if (srs.Count > 0) db.SalesReturns.RemoveRange(srs);

            db.StorageLocations.Where(l => l.WhCode == Warehouse).ExecuteDelete();
            db.Warehouses.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ExecuteDelete();
            var cos = db.Companies.IgnoreQueryFilters().Where(c => c.CompanyCode == Supplier || c.CompanyCode == Customer).ToList();
            if (cos.Count > 0) db.Companies.RemoveRange(cos);
            var prods = db.Products.IgnoreQueryFilters()
                .Where(p => p.ProductCode == ExemptProduct || p.ProductCode == ReturnProduct
                         || p.ProductCode == ReturnProduct2).ToList();
            if (prods.Count > 0) db.Products.RemoveRange(prods);

            db.SaveChanges();
        }

        // ── 測試替身 ──

        private sealed class FakeUser : IUserContext
        {
            private readonly string _plant;
            private readonly string _role;
            public FakeUser(string plant, string role) { _plant = plant; _role = role; }
            public string? PlantId => _plant;
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => "tester";
            public string? ActiveRoleId => _role;
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => new List<string> { _plant };
        }

        private sealed class AllowAllPermission : IPermissionService
        {
            public Task<bool> CanViewData(string ownerRoleId) => Task.FromResult(true);
            public IQueryable<T> FilterByPermission<T>(IQueryable<T> query) where T : class, IOwnedByRole => query;
            public Task<bool> CanRead(string menuKey) => Task.FromResult(true);
            public Task<bool> CanCreate(string menuKey) => Task.FromResult(true);
            public Task<bool> CanUpdate(string menuKey) => Task.FromResult(true);
            public Task<bool> CanDelete(string menuKey) => Task.FromResult(true);
            public Task<bool> CanPrint(string menuKey) => Task.FromResult(true);
        }
    }
}
