using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>不良項目代碼（QCR）CRUD smoke test。</summary>
    public class DefectCodeSmokeTests : CrudSmokeTestBase
    {
        public DefectCodeSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/DefectCode";
        protected override string NewButtonSelector  => "button[data-url$='/DefectCode/Create']";
        protected override string PanelReadySelector => "input[name='Code']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync("#sidePanelBody input[name='Code']", $"E2E-{uniqueSuffix}");
            await page.FillAsync("#sidePanelBody input[name='Name']", $"E2E 測試 {uniqueSuffix}");
        }

        protected override string? ExpectedListText(string uniqueSuffix) => $"E2E-{uniqueSuffix}";

        protected override async Task CleanupAsync(IPage page, string uniqueSuffix)
        {
            try
            {
                var code = $"E2E-{uniqueSuffix}";
                await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true&searchText={Uri.EscapeDataString(code)}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var deleteBtn = page.Locator($"button[onclick*='E2E-{uniqueSuffix}']");
                if (await deleteBtn.CountAsync() == 0) return;
                page.Dialog += async (_, d) => await d.AcceptAsync();
                await deleteBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
            catch { /* best-effort */ }
        }
    }
}
