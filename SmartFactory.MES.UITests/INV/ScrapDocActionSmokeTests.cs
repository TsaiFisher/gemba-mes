using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>報廢單（ScrapDoc）「已存在單據」面板動作守門。</summary>
    public class ScrapDocActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public ScrapDocActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/ScrapDoc";
        protected override string NewButtonSelector  => "button[data-url$='/ScrapDoc/Create']";
        protected override string PanelReadySelector => "#scrapForm";
    }
}
