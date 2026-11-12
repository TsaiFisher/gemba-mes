using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 部門實際費用維護（MFG / roadmap-fin-cst-sub.md §3.3）CRUD smoke test。
    ///
    /// 表單兩個必填欄位皆為 FK 下拉（會計期間、工作中心），於 FillRequiredFieldsAsync
    /// 動態選取現有第一筆選項並記錄，供 CleanupAsync 依「期間 + 工作中心」回頭刪除，
    /// 確保重跑時不會撞唯一鍵（廠別 + 期間 + 工作中心）。
    /// </summary>
    public class DepartmentActualCostSmokeTests : CrudSmokeTestBase
    {
        private string? _period;
        private string? _workCenter;

        public DepartmentActualCostSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/DepartmentActualCost";
        protected override string NewButtonSelector  => "button[data-url$='/DepartmentActualCost/Create']";
        protected override string PanelReadySelector => "select[name='PeriodId']";

        /// <summary>
        /// 確保至少存在一筆會計期間。正式部署已由 AccountingPeriodSeeder 在期間表為空時
        /// 建滿一年，E2E 種子也會補；這裡保留最後一道保險，涵蓋「該 seeder 被組態關閉」
        /// 或「測試庫的期間被清掉」的情況。少了期間，PeriodId 下拉沒有任何選項，
        /// SelectOptionAsync 會空等 30 秒才逾時——症狀離真因很遠。
        /// </summary>
        protected override async Task PrepareAsync(IPage page)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/AccountingPeriod?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // 已有期間就不再建立（期間為全域資料，重跑時必須冪等）
            if (await page.Locator("button[data-action='startClosing']").CountAsync() > 0)
                return;

            // 一定要用具名 handler 並在 finally 解除：dialog handler 是掛在 page 上的，
            // 留著不解會和後面 CleanupAsync 的 handler 同時觸發，第二個收到的是
            // 已處理的 dialog → PlaywrightException 直接打掛 test host（整輪 abort）。
            void OnDialog(object? _, IDialog d) => _ = d.AcceptAsync();
            page.Dialog += OnDialog;
            try
            {
                await page.ClickAsync("button[data-action='createNext']");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
            finally
            {
                page.Dialog -= OnDialog;
            }
        }

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 選現有的第一筆會計期間 / 工作中心（index 0 為空白佔位）
            await page.SelectOptionAsync("#sidePanelBody select[name='PeriodId']", new SelectOptionValue { Index = 1 });
            await page.SelectOptionAsync("#sidePanelBody select[name='WorkCenterCode']", new SelectOptionValue { Index = 1 });

            _period     = await page.InputValueAsync("#sidePanelBody select[name='PeriodId']");
            _workCenter = await page.InputValueAsync("#sidePanelBody select[name='WorkCenterCode']");

            await page.FillAsync("#sidePanelBody input[name='ActualLaborCost']", "1234");
            await page.FillAsync("#sidePanelBody input[name='ActualOverheadCost']", "567");
        }

        protected override async Task CleanupAsync(IPage page, string uniqueSuffix)
        {
            try
            {
                if (string.IsNullOrEmpty(_period) || string.IsNullOrEmpty(_workCenter)) return;

                await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true&periodId={Uri.EscapeDataString(_period)}&workCenterCode={Uri.EscapeDataString(_workCenter)}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var deleteBtn = page.Locator("button[data-action='deleteItem'][data-url$='/DepartmentActualCost/DeleteConfirmed']").First;
                if (await deleteBtn.CountAsync() == 0) return;

                void OnDialog(object? _, IDialog d) => _ = d.AcceptAsync();
                page.Dialog += OnDialog;
                try
                {
                    await deleteBtn.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                }
                finally
                {
                    page.Dialog -= OnDialog;
                }
            }
            catch { /* best-effort */ }
        }
    }
}
