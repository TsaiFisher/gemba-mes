using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// O2C 主幹流程（畫面層）：銷貨訂單 → 出貨單（自訂單匯入）→ 確認（產生應收發票）→
    /// 出庫下架 → 收款核銷。
    ///
    /// 與 SmartFactory.MES.IntegrationTests/Flows/O2CFlowTests 的分工同 P2P：那支釘金額，
    /// 這支釘接線——每一步的按鈕是否存在、是否真的推進到下一步。
    ///
    /// 資料來源為 scripts/seed-e2e-fixture.sql：客戶 E2E-CO01、成品 E2E-FG01
    /// （期初庫存 200 @ E2E-LFG，單位成本 100）、銀行帳戶 E2E-BANK。
    ///
    /// 注意應收發票的產生時點是「出貨單確認」而非「揀料完成」——確認即開票，
    /// 揀料只負責實際扣帳。流程順序依此排列。
    /// </summary>
    public class OrderToCashFlowTests : FlowTestBase
    {
        private const string Customer = "E2E-CO01";
        private const string Product  = "E2E-FG01";
        private const string Uom      = "E2E-EA";
        private const string Currency = "E2E-CUR";
        private const string Tax      = "E2E-TAX";
        private const string Loc      = "E2E-LFG";
        private const string Bank     = "E2E-BANK";

        // 建立前後讀的必須是**同一個列表視圖**，否則「第一列有沒有變」這個判準不成立。
        private const string SoListUrl = "/SalesOrder?searched=true&sortOrder=id_desc&pageSize=20";
        private const string GsListUrl = "/GoodsShipment?searched=true&sortOrder=id_desc&pageSize=20";

        private const decimal Qty       = 5m;
        private const decimal UnitPrice = 300m;

        public OrderToCashFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task SalesOrder_Shipment_Invoice_Pick_Receipt_FullFlow()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            // ── ① 銷貨訂單：建立 → 儲存（未設簽核流程時存檔即核准）─────────────
            // 先記下建立前的第一列：存檔若靜默失敗，這一筆會原封不動留在第一列，
            // ReadFirstRowDocIdAsync 就能當場戳破，而不是把別人的單一路帶下去。
            await GotoAsync(page, SoListUrl);
            var soBefore = await TryReadFirstRowDocIdAsync(page);
            await page.ClickAsync("button[data-url$='/SalesOrder/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #soForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='CompanyCode']").SelectOptionAsync(Customer);
            await page.WaitForTimeoutAsync(400);

            await AddSalesLineAsync(page);

            await SavePanelAsync(page, "#sidePanelBody button[type='submit']", "步驟①：銷貨訂單存檔");

            await GotoAsync(page, SoListUrl);
            var soId = await ReadFirstRowDocIdAsync(page, "步驟①：銷貨訂單列表", soBefore);

            // ── ② 出貨單：自訂單匯入 → 儲存 ─────────────────────────────────────
            await GotoAsync(page, GsListUrl);
            var gsBefore = await TryReadFirstRowDocIdAsync(page);
            await page.ClickAsync("button[data-url$='/GoodsShipment/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #gsForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='CompanyCode']").SelectOptionAsync(Customer);
            await page.WaitForTimeoutAsync(400);

            await ClickActionAsync(page, "importFromSO", 800);
            await page.FillAsync("#soSearchTerm", soId);
            await page.WaitForTimeoutAsync(1000);

            await FillImportQtyAsync(page, "#soLinesTbody", soId, "so-line-qty", Qty);
            await ClickActionAsync(page, "applySOLines", 800);

            var lineCount = await page.Locator("#sidePanelBody #linesTbody tr").CountAsync();
            Assert.True(lineCount == 1,
                $"步驟②：自訂單 {soId} 匯入後，出貨明細應為 1 筆，實際 {lineCount} 筆。" +
                $"多半是 Modal 沒有列出該訂單（訂單須為已核准且尚有未出貨量）。{DescribeDialogs()}");

            await SavePanelAsync(page, "#sidePanelBody button[type='submit']", "步驟②：出貨單存檔");

            await GotoAsync(page, GsListUrl);
            var gsId = await ReadFirstRowDocIdAsync(page, "步驟②：出貨單列表", gsBefore);

            // ── ③ 出貨單確認：轉已確認並自動產生應收發票 ────────────────────────
            await OpenRowPanelAsync(page, gsId, "/GoodsShipment/Edit", "#sidePanelBody #gsForm");

            var confirmDoc = page.Locator("#sidePanelBody [data-doc-action='Confirm']");
            Assert.True(await confirmDoc.CountAsync() > 0,
                $"步驟③：出貨單 {gsId} 的面板沒有「確認」按鈕。確認要求簽核狀態為已核准——" +
                $"多半是存檔時沒有自動核准。{DescribeDialogs()}");
            await confirmDoc.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1800);

            await GotoAsync(page, $"/ARInvoice?searched=true&searchCustomer={Customer}&pageSize=100");
            await AssertVisibleInListAsync(page, gsId,
                "步驟③：應收帳款列表應出現以該出貨單為來源的發票。" +
                "開票是在出貨單確認時由 ARInvoiceService.GenerateFromShipmentAsync 產生的。");

            // ── ④ 出庫下架：實際扣帳 → 出貨單過帳 ───────────────────────────────
            await PickAsync(page, gsId, Loc, Qty, "步驟④：出庫下架");

            await GotoAsync(page, $"/InventoryBalance?searched=true&productCode={Product}&pageSize=50");
            await AssertVisibleInListAsync(page, Loc,
                $"步驟④：揀料後 {Product} 仍應在 {Loc} 有餘量（期初 200 − 出貨 {Qty}）");

            // ── ⑤ 收款：核銷該張發票 → 確認 ─────────────────────────────────────
            await GotoAsync(page, "/ARReceipt?searched=true");
            await page.ClickAsync("button[data-url$='/ARReceipt/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #panelForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='CustomerCode']").SelectOptionAsync(Customer);
            await page.WaitForTimeoutAsync(1200);   // loadOpenInvoices 非同步載入未沖銷發票

            var invoiceRow = page.Locator("#invoiceContainer tr", new PageLocatorOptions { HasText = gsId }).First;
            Assert.True(await invoiceRow.CountAsync() > 0,
                $"步驟⑤：收款單的未沖銷發票清單找不到來源為 {gsId} 的發票。{DescribeDialogs()}");

            // 全額核銷：以該列自身的未沖銷金額為準，不寫死金額。
            var allocInput  = invoiceRow.Locator(".alloc-amt").First;
            var outstanding = await allocInput.GetAttributeAsync("data-outstanding") ?? "0";
            await allocInput.FillAsync(outstanding);
            await page.WaitForTimeoutAsync(300);

            await page.SelectOptionAsync("#sidePanelBody select[name='BankAccountId']", Bank);
            await page.FillAsync("#sidePanelBody input[name='Amount']", outstanding);

            await SavePanelAsync(page, "#sidePanelBody button[type='submit']", "步驟⑤：收款單存檔");

            // 鎖定本次建立的那張收款單再按確認（列表上還有各 smoke test 留下的收款單）。
            // 以 searchStatus=Draft 排除歷次流程測試已確認的收款單（理由同 P2P）。
            await GotoAsync(page,
                $"/ARReceipt?searched=true&searchCustomer={Customer}&searchStatus=Draft&pageSize=50");
            var receiptId = await ReadFirstRowDocIdAsync(page, "步驟⑤：收款單列表");

            var confirmBtn = page.Locator($"[data-action='confirmReceipt'][data-id='{receiptId}']");
            Assert.True(await confirmBtn.CountAsync() > 0,
                $"步驟⑤：收款單 {receiptId} 沒有「確認」按鈕。確認鈕的渲染條件必須與服務層一致" +
                "（只擋已收款／已作廢）——若條件寫成某個從未被設定的狀態，收款單就永遠無法核銷。");
            await confirmBtn.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ⑥ 結果：該張應收發票已收款完畢 ──────────────────────────────────
            // 以 searchStatus 篩選而非比對畫面文字：狀態欄顯示的是在地化字串，
            // 查詢參數比對的是原始值，語系無關。
            await GotoAsync(page,
                $"/ARInvoice?searched=true&searchCustomer={Customer}&searchStatus=Received&pageSize=100");
            await AssertVisibleInListAsync(page, gsId,
                "步驟⑥：核銷確認後，該出貨單的應收發票應出現在 searchStatus=Received 的查詢結果中。" +
                "沒出現代表收款確認沒有回寫發票的已收金額／狀態。");

            Assert.True(JsErrors.Count == 0,
                $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        /// <summary>銷貨訂單面板加一筆明細並填妥數量／單價。</summary>
        private static async Task AddSalesLineAsync(IPage page)
        {
            await page.ClickAsync("#sidePanelBody [data-action='addLine']");
            await page.WaitForTimeoutAsync(500);

            var row = page.Locator("#sidePanelBody #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync(Product);
            await page.WaitForTimeoutAsync(700);

            await row.Locator("input[name$='.OrderQty']").First.FillAsync(Qty.ToString());

            // 幣別與稅別必須明確指定：明細有金額卻沒帶幣別／稅別時，Controller 的行驗證
            // 會擋下存檔（「有金額時必須指定幣別／稅別」）。這兩個下拉沒有預設選項。
            await row.Locator("select[name$='.CurrencyCode']").First.SelectOptionAsync(Currency);
            await page.WaitForTimeoutAsync(400);
            await row.Locator("select[name$='.TaxCode']").First.SelectOptionAsync(Tax);
            await page.WaitForTimeoutAsync(300);

            await row.Locator("input[name$='.UnitPrice']").First.FillAsync(UnitPrice.ToString());
            await page.WaitForTimeoutAsync(300);

            var uom = row.Locator("select[name$='.UomCode']");
            if (await uom.CountAsync() > 0 && string.IsNullOrEmpty(await uom.First.InputValueAsync()))
                await uom.First.SelectOptionAsync(Uom);
        }
    }
}
