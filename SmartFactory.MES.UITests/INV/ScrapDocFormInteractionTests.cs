using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 報廢單（ScrapDoc）_Form 明細「互動」E2E 守門。
    ///
    /// 目的：為 P2 CSP 回補（inline on*= → data-action，批次 B2）建立行為守門。
    /// ScrapDoc/_Form 與 StockTransfer/_Form 同 scaffold（addLine 動態列＋ onProductChange/
    /// onUomChange/onWhChange/onLocChange/lookupSource/lookupStock/removeLine/docAction/
    /// voidDoc），addLine 以模板字串生成的新列本身也帶 inline handler——回補時這些動態列
    /// 同樣要轉 data-action（document 層委派天然支援動態節點）。本測試在回補前後皆適用：
    /// 若任何 handler 失聯，addLine/removeLine/submit 不生效或冒 JS error → 測試紅。
    ///
    /// 不依賴種子資料即可跑 addLine/removeLine/submit/JS-error 守門；onProductChange/
    /// onUomChange 守門需有種子品號/UOM，缺資料時自動跳過該段（不誤紅）。
    /// </summary>
    [Collection("UI Tests")]
    public class ScrapDocFormInteractionTests
    {
        private const string IndexPath         = "/ScrapDoc";
        private const string NewButtonSelector = "button[data-url$='/ScrapDoc/Create']";
        private const string PanelReadySel     = "#scrapForm";

        private readonly UiTestFixture _fixture;
        public ScrapDocFormInteractionTests(UiTestFixture fixture) => _fixture = fixture;

        private async Task<(IPage page, DocFormPage form, List<string> jsErrors)> OpenCreatePanelAsync()
        {
            var page = await _fixture.NewPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            await _fixture.SignInAsync(page);

            var form = new DocFormPage(page, _fixture.BaseUrl);
            await form.GotoIndexAsync(IndexPath);
            await form.OpenNewPanelAsync(NewButtonSelector, PanelReadySel);
            return (page, form, jsErrors);
        }

        // ── ① addLine 動態列：點新增 → 列數 +1，無 JS error ────────────────────
        [Fact]
        public async Task AddLine_AppendsRow_NoJsError()
        {
            var (_, form, jsErrors) = await OpenCreatePanelAsync();
            Assert.True(await form.HasLinesTableAsync(), "_Form 應含明細表 #linesTbody");

            var before = await form.LineCountAsync();
            var after  = await form.AddLineAsync();

            Assert.Equal(before + 1, after);
            Assert.Empty(jsErrors);
        }

        // ── ② removeLine：新增一列後移除 → 列數回到原值，無 JS error ─────────────
        [Fact]
        public async Task RemoveLine_DropsRow_NoJsError()
        {
            var (_, form, jsErrors) = await OpenCreatePanelAsync();

            var before = await form.LineCountAsync();
            await form.AddLineAsync();
            var after = await form.RemoveLastLineAsync();

            Assert.Equal(before, after);
            Assert.Empty(jsErrors);
        }

        // ── ③ onProductChange / onUomChange：選品號＋UOM 不冒 JS error ──────────
        //     （需種子品號/UOM；缺資料自動跳過，不誤紅）
        [Fact]
        public async Task ChangeProductAndUom_NoJsError()
        {
            var (page, form, jsErrors) = await OpenCreatePanelAsync();
            await form.AddLineAsync();

            if (await form.LastLineProductOptionCountAsync() > 0)
            {
                await form.SelectFirstProductInLastLineAsync();
                // onProductChange 內含 fetch /api/warehouse/...；給非同步回呼時間完成
                await page.WaitForTimeoutAsync(800);
            }

            if (await form.LastLineUomOptionCountAsync() > 0)
            {
                await form.SelectFirstUomInLastLineAsync();
                await page.WaitForTimeoutAsync(200);
            }

            Assert.Empty(jsErrors);
        }

        // ── ④ 多列：連續新增三列再逐列移除，列數正確、無 JS error ─────────────────
        [Fact]
        public async Task AddRemoveMultipleLines_CountConsistent_NoJsError()
        {
            var (_, form, jsErrors) = await OpenCreatePanelAsync();

            var baseCount = await form.LineCountAsync();
            await form.AddLineAsync();
            await form.AddLineAsync();
            await form.AddLineAsync();
            Assert.Equal(baseCount + 3, await form.LineCountAsync());

            await form.RemoveLastLineAsync();
            await form.RemoveLastLineAsync();
            await form.RemoveLastLineAsync();
            Assert.Equal(baseCount, await form.LineCountAsync());

            Assert.Empty(jsErrors);
        }

        // ── ⑤ submit 守門：點存檔走 AJAX（data-action-submit="submitPanel" 回傳 false →
        //     dispatcher preventDefault），不得發生原生表單跳頁。──────────────────────
        //     B2 回補最高風險點：onsubmit="...; return false;" → data-action-submit。
        //     若 submitPanel 未回傳 false / 委派沒接上，瀏覽器原生 POST 到 /ScrapDoc/Save
        //     會離開 Index 頁 → URL 變更即判定回歸。
        [Fact]
        public async Task Save_StaysAjax_NoNativeNavigation()
        {
            var (page, _, jsErrors) = await OpenCreatePanelAsync();

            // 攔截空白送出後伺服端 JsonFail 的 alert（避免卡住）
            page.Dialog += (_, d) => _ = d.DismissAsync();

            // 同時等待「對 /ScrapDoc/Save 的 POST 請求」與點擊送出。
            // submitPanel 以 fetch POST 至 form.action；若 handler 失聯則此請求永不發生 → 逾時。
            var reqTask = page.WaitForRequestAsync(
                r => r.Url.Contains("/ScrapDoc/Save", StringComparison.OrdinalIgnoreCase)
                     && r.Method == "POST",
                new PageWaitForRequestOptions { Timeout = 4000 });

            await page.ClickAsync("#sidePanelBody button[type='submit']");

            var req = await reqTask;          // 證明 submit handler 有觸發（AJAX 已送出）
            Assert.NotNull(req);
            Assert.True(req!.IsNavigationRequest == false,
                "送出應為 fetch（非導航請求）；IsNavigationRequest=true 代表原生表單提交未被 preventDefault");

            await page.WaitForTimeoutAsync(300);
            // 仍停在 Index（/ScrapDoc），未被原生 POST 導去 /ScrapDoc/Save
            Assert.DoesNotContain("/ScrapDoc/Save", page.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(jsErrors);
        }
    }
}
