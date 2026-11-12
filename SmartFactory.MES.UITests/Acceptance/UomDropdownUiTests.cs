using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 人工驗收 W-10／W-11／W-12 的自動版：明細選品號後，單位下拉只列「庫存單位＋查得到換算的單位」，
    /// 並依單據預設——採購單→採購單位、銷貨訂單→銷售單位、移轉單／調整單→庫存單位，發退料單只能用庫存單位。
    ///
    /// <para>種子自備（E2E-UC 前綴）：E2E-UC01 庫存單位 E2E-EA、採購 E2E-BOX（1 箱＝12 個）、銷售 E2E-PK（1 包＝6 個），
    /// 另有一個不相連的單位 E2E-ZZ（沒有換算，不得出現）；E2E-RM01（既有種子）沒有任何換算。</para>
    ///
    /// <para>選品號同時觸發 jQuery 與原生 change：select2 只聽 jQuery，data-action-change 委派只聽原生事件。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class UomDropdownUiTests
    {
        private const string Uc = "E2E-UC01";
        private static readonly string[] AllReachable = ["E2E-BOX", "E2E-EA", "E2E-PK"];

        private readonly UiTestFixture _fixture;
        public UomDropdownUiTests(UiTestFixture fixture) => _fixture = fixture;

        private static Task SeedAsync() => SeedFixture.ExecAsync("""
            INSERT INTO "Uoms" ("UomCode","UomName","Remarks","DecimalPlaces","IsActive","CreatedAt","UpdatedAt") VALUES
              ('E2E-BOX','E2E 箱','UomDropdownUiTests',0,true,now(),now()),
              ('E2E-PK','E2E 包','UomDropdownUiTests',0,true,now(),now()),
              ('E2E-ZZ','E2E 無換算','UomDropdownUiTests',0,true,now(),now())
            ON CONFLICT ("UomCode") DO UPDATE SET "IsActive"=true, "UpdatedAt"=now();
            INSERT INTO "Products"
              ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
               "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
               "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
            VALUES ('E2E-UC01','E2E 單位換算品','UomDropdownUiTests',NULL,'E2E-PK','E2E-BOX','E2E-EA',
               'M',0,10,'P',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
            ON CONFLICT ("ProductCode") DO UPDATE SET "StockUom"='E2E-EA',"PurchaseUom"='E2E-BOX',"SalesUom"='E2E-PK',"IsActive"=true;
            DELETE FROM "ProductUomConversions" WHERE "ProductCode"='E2E-UC01';
            INSERT INTO "ProductUomConversions" ("ProductCode","FromUomCode","ToUomCode","Numerator","Denominator") VALUES
              ('E2E-UC01','E2E-BOX','E2E-EA',12,1),
              ('E2E-UC01','E2E-PK','E2E-EA',6,1);
            """);

        public static TheoryData<string, string, string, string, string> Screens => new()
        {
            // 畫面、新增鈕、表單、新增明細鈕、預期預設單位
            { "/PurchaseOrder",   "button[data-url$='/PurchaseOrder/Create']",   "#poForm",       "[data-action='addLine']", "E2E-BOX" },
            { "/SalesOrder",      "button[data-url$='/SalesOrder/Create']",      "#soForm",       "[data-action='addLine']", "E2E-PK" },
            { "/StockTransfer",   "button[data-url$='/StockTransfer/Create']",   "#transferForm", "[data-action='addLine']", "E2E-EA" },
            { "/StockAdjustment", "button[data-url$='/StockAdjustment/Create']", "#adjustForm",   "[data-action='addLine']", "E2E-EA" },
        };

        [Theory]
        [MemberData(nameof(Screens))]
        public async Task 選品號後_單位只列可換算的單位_並依單據預設(string index, string newBtn, string form, string addLine, string expectedDefault)
        {
            await SeedAsync();
            var (page, errors) = await OpenAsync(index, newBtn, form);

            var (options, selected) = await PickProductAsync(page, form, addLine, Uc);
            Assert.Equal(AllReachable, options.OrderBy(o => o, StringComparer.Ordinal));
            Assert.DoesNotContain("E2E-ZZ", options);
            Assert.Equal(expectedDefault, selected);

            var (plain, plainSel) = await PickProductAsync(page, form, addLine, "E2E-RM01");
            Assert.Equal(new[] { "E2E-EA" }, plain);   // 沒有換算的品號：只剩庫存單位
            Assert.Equal("E2E-EA", plainSel);
            Assert.Empty(errors);
        }

        [Fact]
        public async Task 發退料單_選品號後只有庫存單位一個選項()
        {
            await SeedAsync();
            var (page, errors) = await OpenAsync("/MaterialSlip", "button[data-url*='/MaterialSlip/Form?category']", "#materialSlipForm");
            var (options, selected) = await PickProductAsync(page, "#materialSlipForm", "[data-action='addMsLine']", Uc);
            Assert.Equal(new[] { "E2E-EA" }, options);
            Assert.Equal("E2E-EA", selected);
            Assert.Empty(errors);
        }

        [Fact]
        public async Task 發退料單_來源單據類型是下拉_委外類別才多出委外採購單_來源單號依類別重新載入()
        {
            var (page, errors) = await OpenAsync("/MaterialSlip", "button[data-url*='/MaterialSlip/Form?category']", "#materialSlipForm");
            var type = page.Locator("#msSourceType");
            Assert.Equal("SELECT", await type.EvaluateAsync<string>("e => e.tagName"));
            Assert.Equal("SELECT", await page.Locator("#msSourceDoc").EvaluateAsync<string>("e => e.tagName"));
            Assert.True(await page.Locator("#msSourceDoc + .select2").CountAsync() == 1, "來源單號要是可搜尋的 select2");
            Assert.True(await OpoOptionHiddenAsync(page));

            var category = page.Locator("#materialSlipForm select[name='Category']");
            Assert.Equal(1, await category.CountAsync());   // 新增時類別可選；少了它這支測試就沒測到東西
            var reload = page.WaitForResponseAsync(r => r.Url.Contains("/MaterialSlip/SourceDocs") && r.Url.Contains("category=OutsourceIssue"));
            await category.SelectOptionAsync("OutsourceIssue");
            await reload;
            Assert.False(await OpoOptionHiddenAsync(page));

            await category.SelectOptionAsync("InternalIssue");
            Assert.True(await OpoOptionHiddenAsync(page));
            Assert.Equal("WorkOrder", await type.InputValueAsync());
            Assert.Empty(errors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task<bool> OpoOptionHiddenAsync(IPage page)
            => page.Locator("#msSourceType option[value='OutsourcePurchaseOrder']")
                   .EvaluateAsync<bool>("o => o.classList.contains('d-none')");

        private async Task<(IPage Page, List<string> Errors)> OpenAsync(string index, string newBtn, string form)
        {
            var page = await _fixture.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            await _fixture.SignInAsync(page);
            var crud = new DocFormPage(page, _fixture.BaseUrl);
            await crud.GotoIndexAsync(index);
            await crud.OpenNewPanelAsync(newBtn, form);
            return (page, errors);
        }

        /// <summary>新增一列、選品號，等 uom-step.js 依品號重排單位後回傳（選項、選定值）。</summary>
        private static async Task<(string[] Options, string Selected)> PickProductAsync(IPage page, string form, string addLine, string product)
        {
            var before = await page.Locator($"{form} select[name$='.ProductCode']").CountAsync();
            await page.Locator($"{form} {addLine}").First.ClickAsync();
            await page.WaitForFunctionAsync(
                $"n => document.querySelectorAll(\"{form} select[name$='.ProductCode']\").length > n", before);

            await page.EvaluateAsync(@"([form, code]) => {
                const sels = document.querySelectorAll(form + "" select[name$='.ProductCode']"");
                const sel = sels[sels.length - 1];
                if (![...sel.options].some(o => o.value === code)) throw new Error('品號清單沒有 ' + code);
                sel.value = code;
                if (window.jQuery) window.jQuery(sel).trigger('change');
                sel.dispatchEvent(new Event('change', { bubbles: true }));
            }", new object[] { form, product });

            await page.WaitForFunctionAsync(@"([form, code]) => {
                const sels = document.querySelectorAll(form + "" select[name$='.UomCode']"");
                const u = sels[sels.length - 1];
                return u && u.dataset.uomRestricted === code && u.options.length > 0;
            }", new object[] { form, product }, new PageWaitForFunctionOptions { Timeout = 8_000 });

            var r = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"form => {
                const sels = document.querySelectorAll(form + "" select[name$='.UomCode']"");
                const u = sels[sels.length - 1];
                return { options: [...u.options].map(o => o.value).filter(v => v), selected: u.value };
            }", form);
            return (r.GetProperty("options").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                    r.GetProperty("selected").GetString()!);
        }
    }
}
