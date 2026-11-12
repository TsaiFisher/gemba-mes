using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.FIN
{
    /// <summary>
    /// 應付付款（APPayment）面板動作守門。
    ///
    /// 新增走 _CreateForm（與重開時的 _Form 不是同一個檔），正是本測試要守的
    /// 「新增時好好的，存檔後重開就失效」那個縫。
    /// 選廠商會觸發 data-action-change="loadOpenInvoices" 非同步載入未沖銷發票，
    /// 故選完要讓它跑完再填後續欄位。
    /// </summary>
    public class APPaymentActionSmokeTests : DocActionSmokeTestBase
    {
        public APPaymentActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _supplierCode = "";

        protected override string IndexPath          => "/APPayment";
        protected override string NewButtonSelector  => "button[data-url$='/APPayment/Create']";
        protected override string PanelReadySelector => "#panelForm";
        // 重開走唯讀 Details（_Form 無 form 元素，只有 fieldset），與新增的 _CreateForm 不同。
        protected override string ReopenedPanelReadySelector => "fieldset";

        // 付款單重開為唯讀，刻意不再有動作鈕；改驗唯讀面板正常渲染。
        protected override bool ReopenedDocHasActions => false;

        // 列表只提供 Details（付款單多為終結型），base 預設候選已含 Details。
        protected override string ReopenIndexQuery => $"searchSupplier={_supplierCode}";


        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 固定選種子 E2E-CO01：金額必須等於核銷合計且至少一筆核銷（#211），
            // 只有它有種子 ⑲-b 的未結發票 E2E-API02 可核銷；選 Index=1 會挑到沒有發票的公司。
            await page.Locator($"{PanelBodySel} select[name='SupplierCode']").SelectOptionAsync("E2E-CO01");
            _supplierCode = "E2E-CO01";

            // loadOpenInvoices 非同步載入；等目標發票那列出現，而不是睡固定時間。
            var alloc = page.Locator("#invoiceContainer tr", new PageLocatorOptions { HasText = "E2E-API02" })
                            .Locator(".alloc-amt").First;
            await Assertions.Expect(alloc).ToBeVisibleAsync();
            await alloc.FillAsync("100");

            await page.FillAsync($"{PanelBodySel} input[name='PaymentDate']", "2026-01-15");
            await page.FillAsync($"{PanelBodySel} input[name='Amount']", "100");
        }
    }
}
