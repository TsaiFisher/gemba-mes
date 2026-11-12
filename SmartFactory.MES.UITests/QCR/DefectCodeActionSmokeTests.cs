using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>
    /// 不良代碼（DefectCode）面板動作守門。
    ///
    /// 同 <see cref="FIN.BankAccountActionSmokeTests"/>：主檔類畫面主鍵由使用者輸入，
    /// 故重開時直接指定本次新增的代碼，避免「新增靜默失敗卻打開別人的資料」而假綠。
    /// </summary>
    public class DefectCodeActionSmokeTests : DocActionSmokeTestBase
    {
        public DefectCodeActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _code = "";

        protected override string IndexPath          => "/DefectCode";
        protected override string NewButtonSelector  => "button[data-url='/DefectCode/Create']";
        protected override string PanelReadySelector => "#sideForm";

        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { $"/DefectCode/Edit/{_code}" };

        // 列表依代碼排序又分頁，新建的代碼常不在第一頁；用 Index 的 searchText 縮小。
        protected override string ReopenIndexQuery => $"searchText={_code}";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // Code 上限 20 字元；欄位有 text-uppercase，值本身直接用大寫避免比對落差。
            _code = $"SMK{uniqueSuffix}{Random.Shared.Next(1000, 9999)}";

            // 逐一 await：Playwright 的 IPage 不支援對同一頁面並行操作，
            // 用 Task.WhenAll 併發填欄位會有欄位靜默沒填到。
            await page.FillAsync($"{PanelBodySel} input[name='Code']", _code);
            await page.FillAsync($"{PanelBodySel} input[name='Name']", "Smoke 測試不良代碼");
        }
    }
}
