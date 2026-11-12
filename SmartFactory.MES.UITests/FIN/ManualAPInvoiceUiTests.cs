using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.FIN
{
    /// <summary>
    /// 手動應付發票建單面板（manual-test-plan.md §11 #9 的入口）。
    ///
    /// 這一支釘的是**接線**，不是金額：金額鏈由
    /// `SmartFactory.MES.Tests/FIN/ManualInvoice*Tests` 在服務層釘住。這裡只驗
    /// 「使用者一路點下去，面板開得起來、明細列加得出來、送得出去」——
    /// 而這條縫實際壞過：服務層測試全綠而 app 因循環相依根本起不來。
    ///
    /// 也刻意驗一件**不該有的東西**：明細不得出現品號欄位。手動 AP 依定義不進存貨，
    /// 拿它補「該進存貨卻沒走 GR」的進貨會讓存貨永久少計且沒有任何訊號，
    /// 所以危險路徑是用結構關掉的——欄位一旦被加回來，這支測試要紅。
    /// </summary>
    [Collection("UI Tests")]
    public class ManualAPInvoiceUiTests
    {
        private const string IndexPath   = "/APInvoice?searched=true";
        private const string NewButtonSel = "button[data-url$='/APInvoice/CreateManual']";
        private const string PanelBodySel = "#sidePanelBody";

        private readonly UiTestFixture _fixture;
        public ManualAPInvoiceUiTests(UiTestFixture fixture) => _fixture = fixture;

        private async Task<(IPage page, List<string> jsErrors)> OpenPanelAsync()
        {
            var page = await _fixture.NewPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);
            await _fixture.SignInAsync(page);

            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}");
            await page.ClickAsync(NewButtonSel);
            await page.WaitForSelectorAsync($"{PanelBodySel} #manualApInvoiceForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
            return (page, jsErrors);
        }

        [Fact]
        public async Task 手動建單面板要開得起來且沒有JS錯誤()
        {
            var (page, jsErrors) = await OpenPanelAsync();

            Assert.NotNull(await page.QuerySelectorAsync($"{PanelBodySel} select[name='SupplierCode']"));
            Assert.NotNull(await page.QuerySelectorAsync($"{PanelBodySel} #manualLineTable"));
            Assert.True(jsErrors.Count == 0, string.Join("\n", jsErrors));
        }

        /// <summary>
        /// 明細只收科目，**不得有品號欄位**。這是把危險路徑用結構關掉，
        /// 不是漏做——理由見 <c>ManualAPInvoiceViewModel</c> 的類別註解。
        /// </summary>
        [Fact]
        public async Task 明細不得出現品號欄位()
        {
            var (page, _) = await OpenPanelAsync();

            var productField = await page.QuerySelectorAsync(
                $"{PanelBodySel} [name*='ProductCode'], {PanelBodySel} [name*='productCode']");
            Assert.True(productField == null,
                "手動發票明細出現了品號欄位——手動 AP 不進存貨，拿它補該走進貨單的進貨會讓存貨永久少計且沒有訊號。");
        }

        /// <summary>
        /// 新增列之後索引必須連號。ASP.NET 的集合模型繫結遇到索引斷號會**在斷點截斷**，
        /// 後面的行安靜地不見——沒有錯誤訊息，只有少掉的分錄。
        /// </summary>
        [Fact]
        public async Task 新增與刪除明細列之後索引必須連號()
        {
            var (page, jsErrors) = await OpenPanelAsync();

            await page.ClickAsync($"{PanelBodySel} [data-action='addManualLine']");
            await page.ClickAsync($"{PanelBodySel} [data-action='addManualLine']");
            Assert.Equal(3, await page.Locator($"{PanelBodySel} #manualLineTable tbody tr").CountAsync());

            // 刪中間那一列——這才會製造斷號；刪最後一列的話重編與不重編看不出差別。
            await page.Locator($"{PanelBodySel} #manualLineTable tbody tr").Nth(1)
                      .Locator("[data-action='removeManualLine']").ClickAsync();

            var names = await page.EvaluateAsync<string[]>(
                @"() => Array.from(document.querySelectorAll(
                    '#sidePanelBody #manualLineTable tbody tr select[name$="".AccountCode""]'))
                    .map(el => el.name)");

            Assert.Equal(new[] { "Lines[0].AccountCode", "Lines[1].AccountCode" }, names);
            Assert.True(jsErrors.Count == 0, string.Join("\n", jsErrors));
        }

        /// <summary>至少留一列——最後一列被刪掉的話就沒有可複製的樣板，新增列會失效。</summary>
        [Fact]
        public async Task 最後一列不得被刪掉()
        {
            var (page, _) = await OpenPanelAsync();

            await page.Locator($"{PanelBodySel} [data-action='removeManualLine']").First.ClickAsync();

            Assert.Equal(1, await page.Locator($"{PanelBodySel} #manualLineTable tbody tr").CountAsync());
        }

        /// <summary>沒填廠商與科目時要被擋在面板上，不能靜靜送出去。</summary>
        [Fact]
        public async Task 空白送出要留在面板並顯示驗證訊息()
        {
            var (page, _) = await OpenPanelAsync();

            await page.ClickAsync($"{PanelBodySel} button[type='submit']");
            await page.WaitForTimeoutAsync(600);

            Assert.NotNull(await page.QuerySelectorAsync($"{PanelBodySel} #manualApInvoiceForm"));
        }
    }
}
