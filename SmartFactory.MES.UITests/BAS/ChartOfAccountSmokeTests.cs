using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>
    /// 會計科目維護（BAS）CRUD smoke test。
    ///
    /// ⚠️ 繼承的是共用的 <see cref="CrudSmokeTestBase"/>，**不是** <c>BasCrudSmokeTestBase</c>：
    /// 本畫面走共用的 <c>crud-panel.js</c>／<c>#sidePanel</c> 契約，而 BAS 既有畫面那套
    /// 自洽的 <c>#crudPanel</c> 是基底註解自己標明的「R6 baseline 既定偏離，won't-fix」。
    /// 新畫面沿用偏離只會讓偏離永遠清不掉——所以走標準的那條。
    ///
    /// 這一支的價值不只是 CRUD 走得通：新畫面在單元測試全綠的情況下仍可能整頁 500
    /// （resx key 對不上、`_ViewImports` 沒繼承到、選單沒登錄），而那些**只有真的打開
    /// 這一頁**才會知道。本次就實際踩過一次同類問題——服務注入造成循環相依，
    /// 3791 支單元測試全綠而 app 根本起不來。
    /// </summary>
    public class ChartOfAccountSmokeTests : CrudSmokeTestBase
    {
        public ChartOfAccountSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/ChartOfAccount";
        protected override string NewButtonSelector  => "button[data-url$='/ChartOfAccount/Create']";
        protected override string PanelReadySelector => "input[name='AccountCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 代碼刻意以 9 開頭：1–8 是有意義的科目性質首碼，用它們會讓這支 smoke 建出來的
            // 測試資料混進財報（主檔查無時報表會退回首碼分類）。9 不在分類範圍內。
            await page.FillAsync($"{PanelBodySel} input[name='AccountCode']", $"9E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='AccountName']", $"E2E科目{uniqueSuffix}");
        }
    }
}
