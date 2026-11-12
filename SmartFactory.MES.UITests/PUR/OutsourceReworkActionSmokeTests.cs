using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>
    /// 委外重工單（OutsourceRework）面板動作守門。
    ///
    /// 單號採番、列表新單在前，沿用 base 預設「開列表第一列」。來源引用（原派工/委外採購單/NCR）
    /// 皆為選填（Save 只在有填時才驗存在），唯一硬性必填為 ReworkQty>0，責任歸屬預設 Vendor。
    /// 故無種子前置，只需填重工數量即可存草稿。
    /// </summary>
    public class OutsourceReworkActionSmokeTests : DocActionSmokeTestBase
    {
        public OutsourceReworkActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/OutsourceRework";
        protected override string NewButtonSelector  => "button[data-url$='/OutsourceRework/Create']";
        protected override string PanelReadySelector => "#panelForm";

        // 這張單的流程動作（送出/收回/作廢）掛在 Index 列，不在 _Form 面板內；面板本身只有
        // Save(submit)+Cancel。存檔後以 Draft 重開為可編輯，故第 ④ 條改驗「面板仍有 Save 提交鈕」
        // （dispatcher 走 data-action-submit="submitPanel"），而非預設的 plain [data-action] 按鈕。
        protected override string ExpectedActionSelector => $"{PanelBodySel} button[type='submit']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ReworkQty']", "1");
        }
    }
}
