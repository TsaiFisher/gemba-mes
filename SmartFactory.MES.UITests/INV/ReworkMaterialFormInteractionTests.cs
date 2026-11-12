using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 重工領退料單（ReworkMaterial）_Form 明細「互動」E2E 守門。
    ///
    /// 目的：為 P2 CSP 回補（inline on*= → data-action，批次 B2d）建立行為守門。
    /// 與 StockTransfer/SampleDoc 同 scaffold（addLine 動態列＋ onProductChange/onUomChange/
    /// lookupSource/removeLine/docAction/voidDoc），另有主檔 TypeCode/WorkCenterCode 兩個 select
    /// 原以 onchange="fn(this.value)" 傳值，回補後改 data-action-change + 簽章 (event, el) 自
    /// el.value 取值。本測試守 addLine/removeLine/submit 與 JS-error；品號/UOM 互動需種子，缺
    /// 資料自動跳過（不誤紅）。
    /// </summary>
    [Collection("UI Tests")]
    public class ReworkMaterialFormInteractionTests
    {
        private const string IndexPath         = "/ReworkMaterial";
        private const string NewButtonSelector = "button[data-url$='/ReworkMaterial/Create']";
        private const string PanelReadySel     = "#reworkForm";

        private readonly UiTestFixture _fixture;
        public ReworkMaterialFormInteractionTests(UiTestFixture fixture) => _fixture = fixture;

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
        [Fact]
        public async Task ChangeProductAndUom_NoJsError()
        {
            var (page, form, jsErrors) = await OpenCreatePanelAsync();
            await form.AddLineAsync();

            if (await form.LastLineProductOptionCountAsync() > 0)
            {
                await form.SelectFirstProductInLastLineAsync();
                await page.WaitForTimeoutAsync(800);
            }

            if (await form.LastLineUomOptionCountAsync() > 0)
            {
                await form.SelectFirstUomInLastLineAsync();
                await page.WaitForTimeoutAsync(200);
            }

            Assert.Empty(jsErrors);
        }

        // ── ④ 主檔 select：切換 TypeCode（data-action-change + (e,el) 自 el.value）無 JS error ──
        [Fact]
        public async Task ChangeTypeCode_NoJsError()
        {
            var (page, _, jsErrors) = await OpenCreatePanelAsync();

            await page.SelectOptionAsync("#reworkForm [name='TypeCode']", "RR");
            await page.WaitForTimeoutAsync(150);
            await page.SelectOptionAsync("#reworkForm [name='TypeCode']", "RI");
            await page.WaitForTimeoutAsync(150);

            Assert.Empty(jsErrors);
        }

        // ── ⑤ 多列：連續新增三列再逐列移除，列數正確、無 JS error ─────────────────
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

        // ── ⑥ submit 守門：點存檔走 AJAX（data-action-submit="submitPanel" 回傳 false →
        //     dispatcher preventDefault），不得發生原生表單跳頁。──────────────────────
        [Fact]
        public async Task Save_StaysAjax_NoNativeNavigation()
        {
            var (page, _, jsErrors) = await OpenCreatePanelAsync();

            page.Dialog += (_, d) => _ = d.DismissAsync();

            await page.EvalOnSelectorAsync("#reworkForm", "f => f.noValidate = true");
            await page.EvaluateAsync(@"() => {
                const $f = window.jQuery && jQuery('#reworkForm');
                if ($f && $f.length) {
                    $f.off('submit');
                    $f.removeData('validator');
                    $f.removeData('unobtrusiveValidation');
                }
            }");

            var reqTask = page.WaitForRequestAsync(
                r => r.Url.Contains("/ReworkMaterial/Save", StringComparison.OrdinalIgnoreCase)
                     && r.Method == "POST",
                new PageWaitForRequestOptions { Timeout = 4000 });

            await page.ClickAsync("#sidePanelBody button[type='submit']");

            var req = await reqTask;
            Assert.NotNull(req);
            Assert.True(req!.IsNavigationRequest == false,
                "送出應為 fetch（非導航請求）；IsNavigationRequest=true 代表原生表單提交未被 preventDefault");

            await page.WaitForTimeoutAsync(300);
            Assert.DoesNotContain("/ReworkMaterial/Save", page.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(jsErrors);
        }
    }
}
