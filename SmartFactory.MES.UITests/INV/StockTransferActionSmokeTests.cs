using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>庫存調撥單（StockTransfer）「已存在單據」面板動作守門。</summary>
    public class StockTransferActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public StockTransferActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/StockTransfer";
        protected override string NewButtonSelector  => "button[data-url$='/StockTransfer/Create']";
        protected override string PanelReadySelector => "#transferForm";
    }
}
