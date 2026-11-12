using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>不合格品單 NCR（QCR）CRUD smoke test。</summary>
    public class NcrSmokeTests : CrudSmokeTestBase
    {
        public NcrSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Ncr";
        protected override string NewButtonSelector  => "button[data-url$='/Ncr/Create']";
        protected override string PanelReadySelector => "input[asp-for='ProductCode'], input[name='ProductCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // ProductCode 與 Qty 為必填（有 required 屬性）。品號／單位要是主檔裡真的有的——下拉參照檢查啟用後
            // 隨手填的 PROD-xxx／PCS 會被擋（2026-10-16）。用 E2E 種子的成品與它的庫存單位。
            await page.FillAsync("#sidePanelBody input[name='ProductCode']", "E2E-FG01");
            await page.FillAsync("#sidePanelBody input[name='Qty']",         "1");
            await page.FillAsync("#sidePanelBody input[name='UomCode']",      "E2E-EA");
            // 預設處置是報廢，存檔即要求倉庫與儲位（2026-10-27）
            await page.SelectOptionAsync("#sidePanelBody #whSel", "E2E-WRAW");
            await page.FillAsync("#sidePanelBody input[name='LocationCode']", "E2E-LRAW");
        }
    }
}
