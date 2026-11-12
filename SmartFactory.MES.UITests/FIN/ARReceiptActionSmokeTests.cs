using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.FIN
{
    /// <summary>
    /// 應收收款（ARReceipt）面板動作守門。結構與
    /// <see cref="APPaymentActionSmokeTests"/> 對稱：新增走 _CreateForm、重開走 _Form。
    /// </summary>
    public class ARReceiptActionSmokeTests : DocActionSmokeTestBase
    {
        public ARReceiptActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _customerCode = "";

        protected override string IndexPath          => "/ARReceipt";
        protected override string NewButtonSelector  => "button[data-url$='/ARReceipt/Create']";
        protected override string PanelReadySelector => "#panelForm";
        // 重開走唯讀 Details（_Form 無 form 元素，只有 fieldset），與新增的 _CreateForm 不同。
        protected override string ReopenedPanelReadySelector => "fieldset";

        // 收款單重開為唯讀，刻意不再有動作鈕；改驗唯讀面板正常渲染。
        protected override bool ReopenedDocHasActions => false;

        protected override string ReopenIndexQuery => $"searchCustomer={_customerCode}";


        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 固定選種子 E2E-CO01：金額必須等於核銷合計且至少一筆核銷（#211），
            // 只有它有種子 ⑲-b 的未結發票 E2E-ARI02 可核銷；選 Index=1 會挑到沒有發票的公司。
            await page.Locator($"{PanelBodySel} select[name='CustomerCode']").SelectOptionAsync("E2E-CO01");
            _customerCode = "E2E-CO01";

            // loadOpenInvoices 非同步載入；等目標發票那列出現，而不是睡固定時間。
            var alloc = page.Locator("#invoiceContainer tr", new PageLocatorOptions { HasText = "E2E-ARI02" })
                            .Locator(".alloc-amt").First;
            await Assertions.Expect(alloc).ToBeVisibleAsync();
            await alloc.FillAsync("100");

            await page.FillAsync($"{PanelBodySel} input[name='ReceiptDate']", "2026-01-15");
            await page.FillAsync($"{PanelBodySel} input[name='Amount']", "100");
        }
    }
}
