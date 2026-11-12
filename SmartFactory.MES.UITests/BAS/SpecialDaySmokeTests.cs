using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>特殊日維護（BAS）CRUD smoke test。</summary>
    public class SpecialDaySmokeTests : BasCrudSmokeTestBase
    {
        public SpecialDaySmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/SpecialDay";
        protected override string NewButtonSelector  => "button[data-url$='/SpecialDay/Create']";
        protected override string PanelReadySelector => "input[name='Date']";

        /// <summary>
        /// 由 uniqueSuffix 導出遠期唯一日期（Controller 以「同日期已存在」擋重複）。
        ///
        /// 用完整的 10 位後綴取模，而不是只取末 5 位：末 5 位只有 3000 個可能值，
        /// 一旦有殘留資料累積，撞號就會以「ValidationError」的形式間歇性紅燈
        /// （2026-07-23 實測到兩次）。放大到 30000 天（約 82 年）讓撞號機率可以忽略。
        ///
        /// <see cref="CleanupAsync"/> 仍然保留——它是 best-effort（面板遮罩等時序問題會
        /// 讓刪除靜默失敗），所以不能當成唯一防線，兩者要一起做。
        /// </summary>
        private static string DateForSuffix(string uniqueSuffix)
            => new DateTime(2090, 1, 1).AddDays(int.Parse(uniqueSuffix) % 30000).ToString("yyyy-MM-dd");

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // Controller 以「同日期已存在」擋重複 → 從 suffix 導出遠期唯一日期
            await page.FillAsync($"{PanelBodySel} input[name='Date']", DateForSuffix(uniqueSuffix));
            await page.SelectOptionAsync($"{PanelBodySel} select[name='DayType']", "Holiday");
        }

        protected override async Task CleanupAsync(IPage page, string uniqueSuffix)
        {
            try
            {
                var date = DateForSuffix(uniqueSuffix);

                // Index 以 year 篩選，pageSize 放大確保目標列在同一頁
                await page.GotoAsync(
                    $"{_fixture.BaseUrl}{IndexPath}?searched=true&year={date[..4]}&pageSize=1000");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var deleteBtn = page.Locator(
                    $"button[data-action='deleteItem'][data-url*='date={date}']");
                if (await deleteBtn.CountAsync() == 0) return;

                // 具名 handler + finally 解除：dialog handler 掛在 page 上，
                // 留著會與其他 handler 重複處理同一個 dialog 而炸掉 test host。
                void OnDialog(object? _, IDialog d) => _ = d.AcceptAsync();
                page.Dialog += OnDialog;
                try
                {
                    await deleteBtn.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                }
                finally
                {
                    page.Dialog -= OnDialog;
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// 覆寫：SpecialDay 表單使用原生 required 屬性，空白送出被瀏覽器
        /// constraint validation 擋下（不發 fetch、無伺服端驗證訊息），
        /// 改斷言表單為 invalid 且面板未跳頁。
        /// </summary>
        [Fact]
        public override async Task NewPanel_EmptySubmit_ShowsValidationError_StaysOnPanel()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            await page.ClickAsync($"{PanelBodySel} button[type='submit']");
            await page.WaitForTimeoutAsync(500);

            var formValid = await page.EvaluateAsync<bool>(
                $"() => document.querySelector('{PanelBodySel} form').checkValidity()");
            Assert.False(formValid, "空白表單不應通過原生驗證");

            var body = await page.InnerHTMLAsync(PanelBodySel);
            Assert.False(string.IsNullOrWhiteSpace(body), "面板 body 不應因空白送出而清空");
        }
    }
}
