using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>庫存調整單（StockAdjustment）「已存在單據」面板動作守門。</summary>
    public class StockAdjustmentActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public StockAdjustmentActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/StockAdjustment";
        protected override string NewButtonSelector  => "button[data-url$='/StockAdjustment/Create']";
        protected override string PanelReadySelector => "#adjustForm";
    }
}
