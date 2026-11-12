using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>量測儀器主檔（QCR）CRUD smoke test。</summary>
    public class InstrumentSmokeTests : CrudSmokeTestBase
    {
        public InstrumentSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Instrument";
        protected override string NewButtonSelector  => "button[data-url$='/Instrument/Create']";
        protected override string PanelReadySelector => "input[name='InstrumentCode']";

        // Remarks 原本是無上限的 PG text，曾以 AllowedUnboundedTextNames 豁免；
        // 已於 AlignRemarksMaxLength migration 對齊成 varchar(500)，豁免隨之移除。

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // InstrumentCode 與 InstrumentName 為必填（有 required 屬性）
            await page.FillAsync("#sidePanelBody input[name='InstrumentCode']",  $"E2E{uniqueSuffix}");
            await page.FillAsync("#sidePanelBody input[name='InstrumentName']",  $"E2E 儀器 {uniqueSuffix}");
        }

        protected override string? ExpectedListText(string uniqueSuffix) => $"E2E{uniqueSuffix}";

        protected override async Task CleanupAsync(IPage page, string uniqueSuffix)
        {
            try
            {
                var code = $"E2E{uniqueSuffix}";
                await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true&searchCode={Uri.EscapeDataString(code)}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var deleteBtn = page.Locator($"button[onclick*='E2E{uniqueSuffix}']");
                if (await deleteBtn.CountAsync() == 0) return;
                page.Dialog += async (_, d) => await d.AcceptAsync();
                await deleteBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
            catch { /* best-effort */ }
        }
    }
}
