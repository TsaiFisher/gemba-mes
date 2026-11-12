using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 到岸成本分攤單（LandedCostAllocation）_Form「互動」E2E 守門。
    ///
    /// 目的：為 P2 CSP 回補（inline on*= → data-action，批次 B2d）建立行為守門。
    /// 與其他 INV 單據不同：明細不以 addLine 動態列產生，而是透過 modal 進貨明細選入器
    /// （openGrPicker → loadGrLines → addSelectedGrLines）。回補把這些 onclick 與主檔
    /// CurrencyCode 的 onchange、removeLine、docAction 全轉 data-action(-change) + (event, el)
    /// 簽章。本測試守「openGrPicker 委派開啟 modal（含搬至 document.body 避免灰屏）」與
    /// submit 委派走 AJAX，皆不冒 JS error；GR 明細需種子，故不測實際選入。
    /// </summary>
    [Collection("UI Tests")]
    public class LandedCostAllocationFormInteractionTests
    {
        private const string IndexPath         = "/LandedCostAllocation";
        private const string NewButtonSelector = "button[data-url$='/LandedCostAllocation/Create']";
        private const string PanelReadySel     = "#lcaForm";

        private readonly UiTestFixture _fixture;
        public LandedCostAllocationFormInteractionTests(UiTestFixture fixture) => _fixture = fixture;

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

        // ── ① 面板開啟：_Form 與明細表載入，無 JS error ────────────────────────
        [Fact]
        public async Task OpenPanel_FormReady_NoJsError()
        {
            var (page, form, jsErrors) = await OpenCreatePanelAsync();

            Assert.True(await form.HasLinesTableAsync(), "_Form 應含明細表 #linesTbody");
            Assert.Equal(1, await page.Locator("#lcaForm").CountAsync());
            Assert.Empty(jsErrors);
        }

        // ── ② openGrPicker 委派：點「選入進貨明細」→ modal 顯示（data-action="openGrPicker"
        //     委派觸發 + 搬至 document.body 避免 offcanvas 內灰屏），無 JS error。──────────
        [Fact]
        public async Task OpenGrPicker_ShowsModal_NoJsError()
        {
            var (page, _, jsErrors) = await OpenCreatePanelAsync();

            // 「選入進貨明細」按鈕用 bi-search 圖示，且 data-action="openGrPicker"
            await page.ClickAsync("#sidePanelBody button:has(i.bi-search)");

            // modal 顯示後帶 .show 且 display:block；等候其可見
            await page.WaitForSelectorAsync("#grPickerModal.show",
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 4000 });

            Assert.True(await page.Locator("#grPickerModal.show").IsVisibleAsync(),
                "openGrPicker 委派應開啟選入器 modal");
            Assert.Empty(jsErrors);
        }

        // ── ③ submit 守門：點存檔走 AJAX（data-action-submit="submitPanel" 回傳 false →
        //     dispatcher preventDefault），不得發生原生表單跳頁。──────────────────────
        //     DocDate/TotalLandedCost 為 required → 解除 jQuery unobtrusive 驗證攔截，
        //     使 submit 必達 data-action dispatcher。
        [Fact]
        public async Task Save_StaysAjax_NoNativeNavigation()
        {
            var (page, _, jsErrors) = await OpenCreatePanelAsync();

            page.Dialog += (_, d) => _ = d.DismissAsync();

            await page.EvalOnSelectorAsync("#lcaForm", "f => f.noValidate = true");
            await page.EvaluateAsync(@"() => {
                const $f = window.jQuery && jQuery('#lcaForm');
                if ($f && $f.length) {
                    $f.off('submit');
                    $f.removeData('validator');
                    $f.removeData('unobtrusiveValidation');
                }
            }");

            var reqTask = page.WaitForRequestAsync(
                r => r.Url.Contains("/LandedCostAllocation/Save", StringComparison.OrdinalIgnoreCase)
                     && r.Method == "POST",
                new PageWaitForRequestOptions { Timeout = 4000 });

            await page.ClickAsync("#sidePanelBody button[type='submit']");

            var req = await reqTask;
            Assert.NotNull(req);
            Assert.True(req!.IsNavigationRequest == false,
                "送出應為 fetch（非導航請求）；IsNavigationRequest=true 代表原生表單提交未被 preventDefault");

            await page.WaitForTimeoutAsync(300);
            Assert.DoesNotContain("/LandedCostAllocation/Save", page.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(jsErrors);
        }
    }
}
