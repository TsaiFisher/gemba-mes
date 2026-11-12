using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>工作中心維護（BAS）CRUD smoke test。</summary>
    public class WorkCenterSmokeTests : BasCrudSmokeTestBase
    {
        public WorkCenterSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/WorkCenter";
        protected override string NewButtonSelector  => "button[data-url$='/WorkCenter/Create']";
        protected override string PanelReadySelector => "input[name='WorkCenterCode']";

        // 費率/金額欄位（step 依本位幣小數位或費率精度，CLAUDE.md 金額精度規範）
        protected override IReadOnlySet<string> AllowedSmallStepNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
               { "MachineHourlyRate", "StandardLaborCost", "AdditionalRate" };

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='WorkCenterCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='WorkCenterName']", $"E2E工作中心{uniqueSuffix}");
        }
    }
}
