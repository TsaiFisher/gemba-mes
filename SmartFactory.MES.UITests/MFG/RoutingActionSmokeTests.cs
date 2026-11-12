using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 途程（Routing）面板動作守門。MFG 主檔類 <see cref="DocActionSmokeTestBase"/>。
    ///
    /// 主檔類：主鍵 RoutingId 由使用者輸入、列表依代碼排序又分頁，剛建那筆常不在第一頁，
    /// 故沿用 BankAccount 的做法——記下本次代碼、以 <see cref="OpenUrlFragments"/> 指定它，
    /// 並用 <see cref="ReopenIndexQuery"/> 的 searchText 縮小列表，「重開得到」才算數，
    /// 順帶把「新增真的有落地」一併釘住。
    ///
    /// 工序（operationsJson）為選填——空途程可存檔（見 RoutingController.Create），
    /// 故填表只需 RoutingId + ProductCode，不必走工序 modal 子流程。
    /// </summary>
    public class RoutingActionSmokeTests : DocActionSmokeTestBase
    {
        public RoutingActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _routingId = "";

        protected override string IndexPath          => "/Routing";
        protected override string NewButtonSelector  => "button[data-url$='/Routing/Create']";
        protected override string PanelReadySelector => "#routingForm";

        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { $"/Routing/Edit/{_routingId}", $"/Routing/Details/{_routingId}" };

        protected override string ReopenIndexQuery => $"searchText={_routingId}";

        // 途程本面板的動作是表單自身的 data-action-submit="submitRoutingForm"
        // （沒有獨立的 data-action 動作鈕），第 ④ 條須一併認 data-action-submit。
        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // uniqueSuffix 只到「分鐘」，同分鐘內 4 個 [Fact] 會撞主鍵，再補隨機碼；上限 30 字。
            _routingId = $"SMKRT{uniqueSuffix}{Random.Shared.Next(1000, 9999)}";

            // RoutingId：新增走非 readonly 的那個輸入框（Edit 模式那個是 readonly）。
            await page.FillAsync($"{PanelBodySel} input[name='RoutingId']:not([readonly])", _routingId);

            // ProductCode：選第一個實際品號（seed 有 17 筆），避免寫死代碼。
            await page.SelectOptionAsync($"{PanelBodySel} select.rt-product",
                new SelectOptionValue { Index = 1 });
        }
    }
}
