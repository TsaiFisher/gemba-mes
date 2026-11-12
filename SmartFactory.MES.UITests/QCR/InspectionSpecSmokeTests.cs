using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>檢驗規範（QCR）CRUD smoke test。</summary>
    public class InspectionSpecSmokeTests : CrudSmokeTestBase
    {
        public InspectionSpecSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/InspectionSpec";
        protected override string NewButtonSelector  => "button[data-url$='/InspectionSpec/Create']";
        protected override string PanelReadySelector => "input[name='SpecCode']";

        // Remarks 原本是無上限的 PG text，曾以 AllowedUnboundedTextNames 豁免；
        // 已於 AlignRemarksMaxLength migration 對齊成 varchar(500)，豁免隨之移除。

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync("#sidePanelBody input[name='SpecCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync("#sidePanelBody input[name='SpecName']", $"E2E 規範 {uniqueSuffix}");
        }
    }
}
