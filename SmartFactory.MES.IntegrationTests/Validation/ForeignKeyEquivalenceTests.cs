using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.BAS.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Validation
{
    /// <summary>
    /// 無效輸入等價類：**非本廠代碼**與**已停用主檔代碼**。
    ///
    /// 這兩類原本掛在 <c>CrudSmokeTestBase</c> 的註解裡「留給各畫面自補」，
    /// 而至今沒有任何派生類補過。之所以補不起來，是因為當時把它們當成 UI 測試在想：
    /// 畫面上這些欄位一律是 <c>&lt;select&gt;</c>，選項早就被廠別與 IsActive 過濾掉了，
    /// **UI 根本選不到非法值**——寫在 Playwright 裡只能證明下拉選單過濾正確，
    /// 證明不了伺服端會不會收。
    ///
    /// 真正要守的是伺服端：任何人都能直接 POST 一組表單欄位，繞過下拉。
    /// 故這兩類改以整合測試表達，直接對 Controller 的 Save/Create 端點送出
    /// 「其餘欄位全部合法、只有一個外來參照欄位是非法值」的請求。
    ///
    /// 判定方式刻意不看回應內容：各 controller 擋下時的回法並不一致
    /// （PartialView + ModelState、Json success=false、BadRequest 都有）。
    /// 唯一不會誤判的標準是**資料庫裡有沒有多出那一列**。
    ///
    /// 掛 "ScheduleIntegration" collection：那是既有唯一帶 TestAuthHandler 的已登入
    /// collection（admin / 廠別 P1 / 角色 1）。不新增 collection——新增第三個會與既有
    /// 兩個平行共用同一測試庫，觸發 migration 與種子的雙 23505 競態。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ForeignKeyEquivalenceTests : IDisposable
    {
        private const string Plant      = "P1";
        private const string OtherPlant = "FKTP2";
        private const string Prefix     = "FKT";

        // 種子出來的「非法但存在」的主檔代碼
        private const string WhOtherPlant  = Prefix + "-WH-X";    // 他廠倉庫
        private const string WhInactive    = Prefix + "-WH-OFF";  // 本廠但已停用
        private const string SgOtherPlant  = Prefix + "-SG-X";    // 他廠班別群組
        private const string SgInactive    = Prefix + "-SG-OFF";
        private const string UomInactive   = Prefix + "-UOM-OFF"; // 全域主檔，已停用
        private const string CatInactive   = Prefix + "-CAT-OFF";

        // 完全合法的對照組（本廠、啟用中）
        private const string WhValid  = Prefix + "-WH-OK";
        private const string SgValid  = Prefix + "-SG-OK";
        private const string UomValid = Prefix + "-UOM-OK";
        private const string CatValid = Prefix + "-CAT-OK";

        private readonly AuthedScheduleFactory _factory;

        public ForeignKeyEquivalenceTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            SeedAsync().GetAwaiter().GetResult();
        }

        // ════════════════════════════════════════════════════════════════════
        // 正向對照組
        //
        // 沒有這三格，上面每一條「不得建立」都可能是**假綠**：POST 因為任何理由失敗
        // （欄位漏填、model binding 出錯、路由打錯、權限不足），資料庫一樣不會多出那一列，
        // 斷言照樣通過，而我們什麼都沒驗到。對照組證明同一組表單在 FK 值合法時建得起來，
        // 因此上面的失敗只可能來自那個被換掉的欄位。
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public Task Control_ProductCategory_AllReferencesValid_IsAccepted()
            => AssertAcceptedAsync(
                "/ProductCategory/Create",
                ProductCategoryForm(Prefix + "-COK", warehouse: WhValid, stockUom: UomValid),
                db => db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Prefix + "-COK"),
                "產品類別");

        [Fact]
        public Task Control_WorkCenter_AllReferencesValid_IsAccepted()
            => AssertAcceptedAsync(
                "/WorkCenter/Create",
                WorkCenterForm(Prefix + "-WOK", shiftGroup: SgValid, stagingWh: WhValid),
                db => db.WorkCenters.IgnoreQueryFilters().AnyAsync(w => w.WorkCenterCode == Prefix + "-WOK"),
                "工作中心");

        [Fact]
        public Task Control_Product_AllReferencesValid_IsAccepted()
            => AssertAcceptedAsync(
                "/Product/Create",
                ProductForm(Prefix + "-POK", category: CatValid, stockUom: UomValid),
                db => db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Prefix + "-POK"),
                "品號");

        // ════════════════════════════════════════════════════════════════════
        // ⑭ 非本廠代碼
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public Task ProductCategory_DefaultWarehouse_FromAnotherPlant_IsRejected()
            => AssertRejectedAsync(
                "/ProductCategory/Create",
                ProductCategoryForm(Prefix + "-C1", warehouse: WhOtherPlant),
                db => db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Prefix + "-C1"),
                "產品類別的預設倉庫指到他廠倉庫");

        [Fact]
        public Task WorkCenter_ShiftGroup_FromAnotherPlant_IsRejected()
            => AssertRejectedAsync(
                "/WorkCenter/Create",
                WorkCenterForm(Prefix + "-W1", shiftGroup: SgOtherPlant),
                db => db.WorkCenters.IgnoreQueryFilters().AnyAsync(w => w.WorkCenterCode == Prefix + "-W1"),
                "工作中心的班別群組指到他廠班別群組");

        [Fact]
        public Task WorkCenter_StagingWarehouse_FromAnotherPlant_IsRejected()
            => AssertRejectedAsync(
                "/WorkCenter/Create",
                WorkCenterForm(Prefix + "-W2", stagingWh: WhOtherPlant),
                db => db.WorkCenters.IgnoreQueryFilters().AnyAsync(w => w.WorkCenterCode == Prefix + "-W2"),
                "工作中心的備料倉指到他廠倉庫");

        // ════════════════════════════════════════════════════════════════════
        // ⑮ 已停用主檔代碼
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public Task ProductCategory_DefaultWarehouse_Inactive_IsRejected()
            => AssertRejectedAsync(
                "/ProductCategory/Create",
                ProductCategoryForm(Prefix + "-C2", warehouse: WhInactive),
                db => db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Prefix + "-C2"),
                "產品類別的預設倉庫指到已停用倉庫");

        [Fact]
        public Task ProductCategory_DefaultStockUom_Inactive_IsRejected()
            => AssertRejectedAsync(
                "/ProductCategory/Create",
                ProductCategoryForm(Prefix + "-C3", stockUom: UomInactive),
                db => db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Prefix + "-C3"),
                "產品類別的預設庫存單位指到已停用單位");

        [Fact]
        public Task WorkCenter_ShiftGroup_Inactive_IsRejected()
            => AssertRejectedAsync(
                "/WorkCenter/Create",
                WorkCenterForm(Prefix + "-W3", shiftGroup: SgInactive),
                db => db.WorkCenters.IgnoreQueryFilters().AnyAsync(w => w.WorkCenterCode == Prefix + "-W3"),
                "工作中心的班別群組指到已停用班別群組");

        [Fact]
        public Task Product_Category_Inactive_IsRejected()
            => AssertRejectedAsync(
                "/Product/Create",
                ProductForm(Prefix + "-P1", category: CatInactive),
                db => db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Prefix + "-P1"),
                "品號的產品類別指到已停用類別");

        [Fact]
        public Task Product_StockUom_Inactive_IsRejected()
            => AssertRejectedAsync(
                "/Product/Create",
                ProductForm(Prefix + "-P2", stockUom: UomInactive),
                db => db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Prefix + "-P2"),
                "品號的庫存單位指到已停用單位");

        // ════════════════════════════════════════════════════════════════════
        // 表單（其餘欄位一律填合法值，只有受測欄位是非法的）
        // ════════════════════════════════════════════════════════════════════

        // 未指定的 FK 欄位一律填**合法值**而非空字串。
        //
        // 這點是這組測試成立的前提：留空會讓 [Required] 或 controller 自己的非空檢查先擋下來，
        // 於是「資料庫沒多出那一列」變成必然成立，斷言就永遠是綠的而什麼都沒驗到——
        // Product 那兩格一開始正是這樣假綠的（StockUom / CategoryCode 各自留空）。
        private static Dictionary<string, string> ProductCategoryForm(
            string code, string? warehouse = null, string? stockUom = null) => new()
        {
            ["CategoryCode"]         = code,
            ["CategoryName"]         = code + " 類別",
            ["EnableLotTracking"]    = "false",
            ["DefaultWarehouseCode"] = warehouse ?? WhValid,
            ["DefaultLocationCode"]  = "",
            ["DefaultStockUomCode"]  = stockUom ?? UomValid,
            ["CostMethod"]           = "SystemDefault",
            ["IsActive"]             = "true",
        };

        private static Dictionary<string, string> WorkCenterForm(
            string code, string? shiftGroup = null, string? stagingWh = null) => new()
        {
            ["WorkCenterCode"]      = code,
            ["WorkCenterName"]      = code + " 工作中心",
            ["ShiftGroupCode"]      = shiftGroup ?? SgValid,
            ["IsProductionLine"]    = "false",
            ["AdditionalRate"]      = "0",
            ["StandardLaborCost"]   = "0",
            ["MachineHourlyRate"]   = "0",
            ["StagingWhCode"]       = stagingWh ?? WhValid,
            ["StagingLocationCode"] = "",
            ["IsActive"]            = "true",
        };

        private static Dictionary<string, string> ProductForm(
            string code, string? category = null, string? stockUom = null) => new()
        {
            ["ProductCode"]      = code,
            ["ProductName"]      = code + " 品名",
            ["StockUom"]         = stockUom ?? UomValid,
            ["StockMode"]        = "0",
            ["SafetyStock"]      = "0",
            ["StandardCost"]     = "0",
            ["LeadTimeDays"]     = "0",
            ["CategoryCode"]     = category ?? CatValid,
            ["IsActive"]         = "true",
        };

        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 送出表單，然後斷言那一列**沒有**被建出來。
        ///
        /// 不看 HTTP 狀態也不看回應 body：擋下時各 controller 的回法不一致，
        /// 而「資料庫多了一列」是唯一不會誤判的失敗證據。
        /// </summary>
        private async Task AssertRejectedAsync(
            string url,
            Dictionary<string, string> form,
            Func<ApplicationDbContext, Task<bool>> existsAsync,
            string scenario)
        {
            await PostFormAsync(url, form);

            using var scope = _factory.Services.CreateScope();
            var created = await existsAsync(Db(scope));

            Assert.False(created,
                $"{scenario}：伺服端收下了這筆資料。表單其餘欄位都合法，只有這個外來參照欄位是非法值，"
                + "而下拉選單擋不住直接 POST。Save 前需檢核該代碼存在、屬於本廠、且未停用。");
        }

        /// <summary>對照組：同一組表單、FK 值全部合法 → 必須建得起來。</summary>
        private async Task AssertAcceptedAsync(
            string url,
            Dictionary<string, string> form,
            Func<ApplicationDbContext, Task<bool>> existsAsync,
            string screen)
        {
            await PostFormAsync(url, form);

            using var scope = _factory.Services.CreateScope();
            Assert.True(await existsAsync(Db(scope)),
                $"{screen}：FK 值全部合法的表單也建不起來——同一組表單的「不得建立」斷言因此毫無意義"
                + "（任何理由的失敗都會讓它通過）。先修對照組，再看無效輸入那幾格。");
        }

        private async Task PostFormAsync(string url, Dictionary<string, string> form)
        {
            var (client, token) = await NewAuthedClientAsync();
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(form);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            await client.SendAsync(req);
        }

        private async Task<(HttpClient Client, string Token)> NewAuthedClientAsync()
        {
            var client = _factory.CreateClient();
            var html   = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能取得 AntiForgery token（/Schedule 未渲染或未登入）");
            return (client, m.Groups[1].Value);
        }

        private static ApplicationDbContext Db(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = "1";
            return db;
        }

        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 種子出「存在但不該被本廠採用」的主檔。
        ///
        /// 關鍵在於這些代碼**必須真的存在**：若只塞一個不存在的亂碼，測試就退化成
        /// 在驗「有沒有做存在性檢查」，而漏掉真正的兩類——代碼存在、只是屬於別廠或已停用。
        /// </summary>
        private async Task SeedAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await PurgeAsync(db);

            if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == OtherPlant))
                db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = OtherPlant, Name = "FK 測試他廠" });

            db.Warehouses.AddRange(
                new Warehouse { WhCode = WhOtherPlant, WhName = "他廠倉", PlantId = OtherPlant, IsActive = true },
                new Warehouse { WhCode = WhInactive,   WhName = "停用倉", PlantId = Plant,      IsActive = false },
                new Warehouse { WhCode = WhValid,      WhName = "合法倉", PlantId = Plant,      IsActive = true });

            db.ShiftGroups.AddRange(
                new ShiftGroup { GroupCode = SgOtherPlant, GroupName = "他廠班群", PlantId = OtherPlant, IsActive = true },
                new ShiftGroup { GroupCode = SgInactive,   GroupName = "停用班群", PlantId = Plant,      IsActive = false },
                new ShiftGroup { GroupCode = SgValid,      GroupName = "合法班群", PlantId = Plant,      IsActive = true });

            db.Uoms.AddRange(
                new Uom { UomCode = UomInactive, UomName = "停用單位", IsActive = false },
                new Uom { UomCode = UomValid,    UomName = "合法單位", IsActive = true });

            db.ProductCategories.AddRange(
                new ProductCategory { CategoryCode = CatInactive, CategoryName = "停用類別", IsActive = false },
                new ProductCategory { CategoryCode = CatValid,    CategoryName = "合法類別", IsActive = true });

            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            using var scope = _factory.Services.CreateScope();
            PurgeAsync(Db(scope)).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        private static async Task PurgeAsync(ApplicationDbContext db)
        {
            await db.Products.IgnoreQueryFilters()
                .Where(p => p.ProductCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.WorkCenters.IgnoreQueryFilters()
                .Where(w => w.WorkCenterCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.ProductCategories.IgnoreQueryFilters()
                .Where(c => c.CategoryCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.ShiftGroups.IgnoreQueryFilters()
                .Where(s => s.GroupCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.Warehouses.IgnoreQueryFilters()
                .Where(w => w.WhCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.Uoms.IgnoreQueryFilters()
                .Where(u => u.UomCode.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.Plants.IgnoreQueryFilters()
                .Where(p => p.Id == OtherPlant).ExecuteDeleteAsync();
        }
    }
}
