using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES;
using SmartFactory.MES.Data;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.INV
{
    /// <summary>
    /// 「庫存設定 → 簡化模式」端到端真 PG 整合驗證。
    ///
    /// 單元測試各自釘住兩端：SystemParamEnhancementFilterTests 驗設定畫面的讀寫正規化，
    /// 各單據 ControllerTests 以 mock ISystemParamService 驗分支。中間那一段——
    /// 「使用者在畫面上按下儲存的那個值，真的會被下游單據讀到，而且庫存真的動了」——
    /// 從未被驗證過。本測試補上：真 Filter 寫入 → 真 SystemParamService（含 IMemoryCache
    /// 失效）→ 真 LoanDocController → 真 InventoryPostingService → 真 PostgreSQL 的
    /// StockMovement / Inventory。
    ///
    /// 選 LoanDoc（借出 IL，Direction=-1）為代表：Confirm 有完整的 simplified 分支，
    /// 且不牽涉 AP/AR/GL，讓斷言聚焦在「模式決定庫存動不動」這件事本身。
    ///
    /// 隔離：專屬廠別 ZZSIM + 遠期日 2087-06（他測群聚在 2088/2099），每次重跑先 Purge，
    /// 並刪掉該月會計期間確保「期間不存在→無鎖→放行」。inv_simplified_mode 是全域參數，
    /// 每個測試結束一律清回未設定，避免污染同一測試庫的其他測試。
    /// </summary>
    [Collection("Integration")]
    public class InvSimplifiedModeIntegrationTests : IDisposable
    {
        private const string Plant     = "ZZSIM";
        private const string RoleId    = "1";
        private const string Category  = "SIM-CAT";
        private const string Product   = "SIM-P1";
        private const string Warehouse = "SIM-WH";
        private const string Location  = "SIM-LOC";
        private const string Customer  = "SIM-CUS";
        private const string LoanId    = "SIM-IL1";
        private static readonly DateOnly DocDate = new(2087, 6, 15);

        private readonly TestWebApplicationFactory _factory;

        public InvSimplifiedModeIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        /// <summary>全域參數不得留給後續測試：一律清回未設定。</summary>
        public void Dispose()
        {
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISystemParamService>()
                .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, null).GetAwaiter().GetResult();
        }

        // ═══════════════════════════════════════════════════════════════
        // 端到端：畫面存檔 → 單據確認 → 真庫存
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task SaveSimplifiedOnScreen_ThenConfirmLoanDoc_ActuallyMovesInventory()
        {
            // ① 使用者在「庫存設定」勾選簡化模式並儲存（走真 Filter 的 POST Save 路徑）
            await SaveSettingAsync(new Dictionary<string, string> { ["InvSimplifiedMode"] = "true" });

            // ② 值真的落到 SystemParams 表
            using (var v = _factory.Services.CreateScope())
            {
                var vdb = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var row = await vdb.SystemParams.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Key == ProjectZSystemParamKeys.InvSimplifiedMode);
                Assert.Equal("true", row?.Value);
            }

            // ③ 重新載入設定畫面，勾選狀態要讀得回來（快取已由 SetAsync 失效）
            Assert.Equal(true, await LoadSettingViewDataAsync());

            // ④ 確認一張已核准的借出單
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            var result = await BuildLoanController(scope, db).Confirm(LoanId);
            Assert.IsType<OkObjectResult>(result);

            // ⑤ 真的過帳了：單據 Posted、庫存 100 → 90、產生一筆出庫異動
            using var verify = _factory.Services.CreateScope();
            var vdb2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var doc = await vdb2.LoanDocs.IgnoreQueryFilters().Include(d => d.Lines)
                .FirstAsync(d => d.LoanDocId == LoanId);
            Assert.Equal(DocStatus.Posted, doc.Status);
            Assert.Equal(10m, doc.Lines.Single().QtyPutAway);

            var inv = await vdb2.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.PlantId == Plant && i.ProductCode == Product && i.LocationCode == Location);
            Assert.Equal(90m, inv.QtyOnHand);

            var mv = await vdb2.StockMovements.IgnoreQueryFilters()
                .SingleAsync(m => m.DocNo == LoanId && m.PlantId == Plant);
            Assert.Equal(-1, mv.Direction);
            Assert.Equal(10m, mv.StockQty);
            Assert.Equal("IL", mv.TypeCode);
        }

        [Fact]
        public async Task SaveUncheckedOnScreen_ThenConfirmLoanDoc_LeavesInventoryUntouched()
        {
            // ① 先設成簡化模式，再模擬使用者「取消勾選」後儲存——
            //    checkbox 未勾選時瀏覽器不送該欄位，Filter 必須明確寫回 "false"。
            await SaveSettingAsync(new Dictionary<string, string> { ["InvSimplifiedMode"] = "true" });
            await SaveSettingAsync(new Dictionary<string, string> { ["BaseCurrency"] = "TWD" });

            using (var v = _factory.Services.CreateScope())
            {
                var vdb = v.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var row = await vdb.SystemParams.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Key == ProjectZSystemParamKeys.InvSimplifiedMode);
                Assert.Equal("false", row?.Value);
            }
            Assert.Equal(false, await LoadSettingViewDataAsync());

            // ② 同一張單、同一個 Confirm：完整模式只轉 Confirmed，等倉庫端上/下架才過帳
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            Assert.IsType<OkObjectResult>(await BuildLoanController(scope, db).Confirm(LoanId));

            using var verify = _factory.Services.CreateScope();
            var vdb2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var doc = await vdb2.LoanDocs.IgnoreQueryFilters().FirstAsync(d => d.LoanDocId == LoanId);
            Assert.Equal(DocStatus.Confirmed, doc.Status);

            var inv = await vdb2.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.PlantId == Plant && i.ProductCode == Product && i.LocationCode == Location);
            Assert.Equal(100m, inv.QtyOnHand);       // 一動未動

            Assert.False(await vdb2.StockMovements.IgnoreQueryFilters()
                .AnyAsync(m => m.DocNo == LoanId && m.PlantId == Plant));
        }

        /// <summary>
        /// 參數從未設定（乾淨部署）＝完整模式。這是新部署的實際起點，
        /// 若誤判成簡化模式會在使用者尚未做任何設定前就直接扣庫存。
        /// </summary>
        [Fact]
        public async Task ParamNeverSet_ConfirmBehavesAsFullMode()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            Assert.IsType<OkObjectResult>(await BuildLoanController(scope, db).Confirm(LoanId));

            using var verify = _factory.Services.CreateScope();
            var vdb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(DocStatus.Confirmed,
                (await vdb.LoanDocs.IgnoreQueryFilters().FirstAsync(d => d.LoanDocId == LoanId)).Status);
            Assert.False(await vdb.StockMovements.IgnoreQueryFilters()
                .AnyAsync(m => m.DocNo == LoanId && m.PlantId == Plant));
        }

        /// <summary>
        /// 簡化模式下庫存不足須整筆擋下：不得留下半套（單據已 Posted 但庫存沒動，或反之）。
        /// </summary>
        [Fact]
        public async Task SimplifiedMode_InsufficientStock_RejectsAndRollsBack()
        {
            await SaveSettingAsync(new Dictionary<string, string> { ["InvSimplifiedMode"] = "true" });

            using (var s = _factory.Services.CreateScope())
            {
                var db0 = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db0.Inventories.IgnoreQueryFilters()
                    .Where(i => i.PlantId == Plant && i.ProductCode == Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.QtyOnHand, 3m));   // 借出 10 > 可用 3
            }

            using var scope = _factory.Services.CreateScope();
            var db = Scoped(scope);
            Assert.IsType<BadRequestObjectResult>(await BuildLoanController(scope, db).Confirm(LoanId));

            using var verify = _factory.Services.CreateScope();
            var vdb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(DocStatus.Draft,
                (await vdb.LoanDocs.IgnoreQueryFilters().FirstAsync(d => d.LoanDocId == LoanId)).Status);
            Assert.Equal(3m, (await vdb.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.PlantId == Plant && i.ProductCode == Product)).QtyOnHand);
            Assert.False(await vdb.StockMovements.IgnoreQueryFilters()
                .AnyAsync(m => m.DocNo == LoanId && m.PlantId == Plant));
        }

        // ─────────────────────── 基礎建設 ───────────────────────

        /// <summary>走真 SystemParamEnhancementFilter 的 POST Save 路徑（Core 已成功重導向後的補存）。</summary>
        private async Task SaveSettingAsync(Dictionary<string, string> form)
        {
            using var scope = _factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var filter = new SystemParamEnhancementFilter(
                sp.GetRequiredService<ApplicationDbContext>(),
                sp.GetRequiredService<ISystemParamService>());

            var ctx = BuildActionContext("Save", "POST", form);
            await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(
                new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller)
                {
                    Result = new RedirectToActionResult("Index", "SystemParam", null)
                }));
        }

        /// <summary>走真 Filter 的 GET Index 路徑，回傳畫面實際收到的勾選狀態。</summary>
        private async Task<object?> LoadSettingViewDataAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var filter = new SystemParamEnhancementFilter(
                sp.GetRequiredService<ApplicationDbContext>(),
                sp.GetRequiredService<ISystemParamService>());

            var vr = new ViewResult
            {
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            };
            var ctx = BuildActionContext("Index");
            await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(
                new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller) { Result = vr }));
            return vr.ViewData["InvSimplifiedMode"];
        }

        private static ActionExecutingContext BuildActionContext(
            string action, string method = "GET", Dictionary<string, string>? form = null)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = method;
            if (form != null)
                http.Request.Form = new FormCollection(
                    form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));

            var routeData = new RouteData();
            routeData.Values["controller"] = "SystemParam";
            routeData.Values["action"] = action;

            return new ActionExecutingContext(
                new ActionContext(http, routeData, new ActionDescriptor()),
                new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        }

        private static ApplicationDbContext Scoped(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return db;
        }

        private static LoanDocController BuildLoanController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = scope.ServiceProvider;
            var ctrl = new LoanDocController(
                db,
                new FakeUser(Plant, RoleId),
                new AllowAllPermission(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>());
            ctrl.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = sp }
            };
            return ctrl;
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            Purge(db);

            if (!db.ProductCategories.Any(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                { CategoryCode = Category, CategoryName = "簡化模式整測料", CostMethod = CostMethod.MovingAverage });

            db.Companies.Add(new Company
            { CompanyCode = Customer, CompanyName = "簡化模式整測客戶", IsCustomer = true, IsActive = true });
            db.Products.Add(new Product
            { ProductCode = Product, ProductName = "簡化模式整測品", StockUom = "PC", CategoryCode = Category });
            db.Warehouses.Add(new Warehouse
            { WhCode = Warehouse, WhName = "簡化模式整測倉", PlantId = Plant, WarehouseType = WarehouseType.Raw });
            db.StorageLocations.Add(new StorageLocation
            { WhCode = Warehouse, LocationCode = Location, LocationName = "簡化模式整測儲位" });

            // 期初庫存 100（借出 10 後應剩 90）
            db.Inventories.Add(new Inventory
            {
                PlantId = Plant, ProductCode = Product, LocationCode = Location, LotNo = "",
                QtyOnHand = 100m, UnitCost = 5m,
            });

            // 借出單（IL，Direction=-1）：Draft + Approved，等 Confirm
            var loan = new LoanDoc
            {
                LoanDocId = LoanId, LoanDate = DocDate, TypeCode = "IL", TypeCategory = "Loan",
                EntityType = "Customer", EntityCode = Customer, CreatedBy = "tester",
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            loan.Lines.Add(new LoanDocLine
            {
                LoanDocId = LoanId, LineNo = 1, ProductCode = Product,
                Qty = 10m, UomCode = "PC", StockQty = 10m, StockUomCode = "PC",
                UnitPrice = 5m, WhCode = Warehouse, LocationCode = Location,
            });
            db.LoanDocs.Add(loan);

            db.SaveChanges();
        }

        private static void Purge(ApplicationDbContext db)
        {
            var loans = db.LoanDocs.IgnoreQueryFilters().Include(d => d.Lines)
                .Where(d => d.PlantId == Plant).ToList();
            if (loans.Count > 0) { db.LoanDocs.RemoveRange(loans); db.SaveChanges(); }

            db.StockMovements.IgnoreQueryFilters().Where(m => m.PlantId == Plant).ExecuteDelete();
            db.InventoryReservations.IgnoreQueryFilters().Where(r => r.PlantId == Plant).ExecuteDelete();
            db.Inventories.IgnoreQueryFilters().Where(i => i.PlantId == Plant).ExecuteDelete();

            // 會計期間為全域：刪掉本測試用到的 2087-06，確保「期間不存在→無鎖→放行」
            db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == "208706").ExecuteDelete();

            db.StorageLocations.Where(l => l.WhCode == Warehouse).ExecuteDelete();
            db.Warehouses.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ExecuteDelete();
            var cos = db.Companies.IgnoreQueryFilters().Where(c => c.CompanyCode == Customer).ToList();
            if (cos.Count > 0) db.Companies.RemoveRange(cos);
            var prods = db.Products.IgnoreQueryFilters().Where(p => p.ProductCode == Product).ToList();
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
