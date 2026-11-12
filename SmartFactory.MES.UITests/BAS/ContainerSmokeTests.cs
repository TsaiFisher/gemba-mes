using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>容器維護（BAS）CRUD smoke test。</summary>
    public class ContainerSmokeTests : BasCrudSmokeTestBase
    {
        public ContainerSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Container";
        protected override string NewButtonSelector  => "button[data-url$='/Container/Create']";
        protected override string PanelReadySelector => "input[name='ContainerCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ContainerCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='ContainerName']", $"E2E容器{uniqueSuffix}");
        }
    }
}
