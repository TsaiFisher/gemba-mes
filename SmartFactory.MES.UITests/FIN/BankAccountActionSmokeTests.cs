using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.FIN
{
    /// <summary>
    /// 銀行帳戶（BankAccount）面板動作守門。FIN 家族第一支 <see cref="DocActionSmokeTestBase"/>。
    ///
    /// 主檔類畫面與單據類的關鍵差異：主鍵由使用者輸入而非採番，且列表通常已有種子資料。
    /// 若沿用 base 預設「重開列表第一筆」，那麼即使新增其實靜默失敗，也會因為打開了別人
    /// 的資料而照樣綠——正是 MaterialSlip 靜默建單失敗那一類 bug 的漏測方式。
    /// 因此這裡把本次產生的代碼記下來，<see cref="OpenUrlFragments"/> 直接指定它，
    /// 「重開得到」才算數，順帶把「新增真的有落地」一併釘住。
    /// </summary>
    public class BankAccountActionSmokeTests : DocActionSmokeTestBase
    {
        public BankAccountActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _accountId = "";

        protected override string IndexPath          => "/BankAccount";
        protected override string NewButtonSelector  => "button[data-url='/BankAccount/Create']";
        protected override string PanelReadySelector => "#panelForm";

        // 指定重開「本次新增的那一筆」；base 會在 FillRequiredFieldsAsync 之後才取這個值。
        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { $"/BankAccount/Edit/{_accountId}" };

        // 列表分頁，新建那筆常不在第一頁；用 Index 的 search 參數縮小。
        protected override string ReopenIndexQuery => $"search={_accountId}";

        // 本面板唯一的動作是表單自身的 data-action-submit="submitPanel"（沒有 data-action 按鈕），
        // 故第 ④ 條要一併認 data-action-submit，否則會誤判成「沒有任何動作」。
        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // base 的 uniqueSuffix 只到「分鐘」，同一分鐘內 4 個 [Fact] 會撞主鍵，
            // 故再補一段隨機碼。AccountId 上限 30 字元，這裡 15 字元內。
            _accountId = $"SMK{uniqueSuffix}{Random.Shared.Next(1000, 9999)}";

            // 逐一 await：Playwright 的 IPage 不支援對同一頁面並行操作，
            // 用 Task.WhenAll 併發填欄位會有欄位靜默沒填到。
            // AccountId 同時有隱藏欄位（送出用）與可見輸入框，name 相同；
            // 不排除 hidden 會選到隱藏的那個而卡在「element is not visible」。
            await page.FillAsync($"{PanelBodySel} input[name='AccountId']:not([type=hidden])", _accountId);
            await page.FillAsync($"{PanelBodySel} input[name='BankName']", "Smoke 測試銀行");
            await page.FillAsync($"{PanelBodySel} input[name='AccountNo']", "0000000000");
        }
    }
}
