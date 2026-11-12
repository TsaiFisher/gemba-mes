using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// P2P 主幹流程（畫面層）：採購單 → 進貨單（自採購單匯入）→ 入庫上架 →
    /// 應付發票自動認列 → 付款核銷。
    ///
    /// 與 SmartFactory.MES.IntegrationTests/Flows/P2PFlowTests 的分工：那支直接呼叫服務，
    /// 釘的是「庫存 × 子帳 × 總帳」三方金額一致；這支只走畫面，釘的是**接線**——
    /// 使用者一路點下去，每一步的按鈕是否真的存在、是否真的推進到下一步。這條縫實際
    /// 壞過：面板動作鈕的 handler 沒掛到 window 時 dispatcher 只會 console.warn，
    /// 服務層測試再綠也照樣「按了沒反應」。
    ///
    /// 為什麼寫成單一測試方法：每一步都以前一步的單據狀態為前提，xUnit 不保證方法
    /// 執行順序，拆成多個 [Fact] 只會得到隨機紅燈。
    ///
    /// 資料來源為 scripts/seed-e2e-fixture.sql：供應商 E2E-CO01、料號 E2E-PP01
    /// （IqcExempt=true 免驗直放，否則上架至正常倉會被品質隔離閘門擋下）、
    /// 原料倉 E2E-WRAW/E2E-LRAW、銀行帳戶 E2E-BANK。
    /// </summary>
    public class ProcureToPayFlowTests : FlowTestBase
    {
        private const string Supplier = "E2E-CO01";
        private const string Product  = "E2E-PP01";
        private const string Uom      = "E2E-EA";
        private const string Currency = "E2E-CUR";
        private const string Tax      = "E2E-TAX";
        private const string Wh       = "E2E-WRAW";
        private const string Loc      = "E2E-LRAW";
        private const string Bank     = "E2E-BANK";

        // 建立前後讀的必須是同一個列表視圖，否則「第一列有沒有變」這個判準不成立。
        private const string PoListUrl = "/PurchaseOrder?searched=true&sortOrder=id_desc&pageSize=20";
        private const string GrListUrl = "/GoodsReceipt?searched=true&sortOrder=id_desc&pageSize=20";

        private const decimal Qty       = 10m;
        private const decimal UnitPrice = 50m;

        public ProcureToPayFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task PurchaseOrder_Receipt_PutAway_Invoice_Payment_FullFlow()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            // ── ① 採購單：建立 → 儲存後送審（未設簽核流程時直接核准）────────────
            // 先記下建立前的第一列（理由同 OrderToCashFlowTests）。
            await GotoAsync(page, PoListUrl);
            var poBefore = await TryReadFirstRowDocIdAsync(page);
            await page.ClickAsync("button[data-url$='/PurchaseOrder/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #poForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='SupplierCode']").SelectOptionAsync(Supplier);
            await page.WaitForTimeoutAsync(400);

            await AddPurchaseLineAsync(page);

            // 「儲存後送審」而非「儲存」：採購明細要能被進貨單匯入，採購單必須是已核准
            // （PurchaseOrderController.AvailableLines 只列 ApprovalStatus=Approved）。
            await SavePanelAsync(page, "#sidePanelBody button[value='save_and_submit']", "步驟①：採購單存檔");

            await GotoAsync(page, PoListUrl);
            var poId = await ReadFirstRowDocIdAsync(page, "步驟①：採購單列表", poBefore);

            // ── ② 進貨單：自採購單匯入 → 儲存（未設簽核流程時即核准並轉已確認）──
            await GotoAsync(page, GrListUrl);
            var grBefore = await TryReadFirstRowDocIdAsync(page);
            await page.ClickAsync("button[data-url$='/GoodsReceipt/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #grForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='SupplierCode']").SelectOptionAsync(Supplier);
            await page.WaitForTimeoutAsync(400);

            await ClickActionAsync(page, "openPoModal", 800);
            await page.FillAsync("#poImportSearch", poId);
            await page.WaitForTimeoutAsync(1000);

            await FillImportQtyAsync(page, "#poImportTbody", poId, "po-line-qty", Qty);
            await ClickActionAsync(page, "confirmPoImport", 800);

            var lineCount = await page.Locator("#sidePanelBody #linesTbody tr").CountAsync();
            Assert.True(lineCount == 1,
                $"步驟②：自採購單 {poId} 匯入後，進貨明細應為 1 筆，實際 {lineCount} 筆。" +
                $"多半是 Modal 沒有列出該採購單（採購單須為已核准且未收貨完）。{DescribeDialogs()}");

            await SavePanelAsync(page, "#sidePanelBody button[value='save']", "步驟②：進貨單存檔");

            await GotoAsync(page, GrListUrl);
            var grId = await ReadFirstRowDocIdAsync(page, "步驟②：進貨單列表", grBefore);

            // ── ③ 入庫上架：貨進正常倉（免驗直放）→ 進貨單過帳、就地認列應付 ────
            await PutAwayAsync(page, grId, Wh, Loc, "步驟③：入庫上架");

            await GotoAsync(page, $"/InventoryBalance?searched=true&productCode={Product}&pageSize=50");
            await AssertVisibleInListAsync(page, Loc,
                $"步驟③：上架後 {Product} 應在 {Loc} 出現庫存餘量");

            // ── ④ 應付發票：由上架量自動認列，來源單為這張進貨單 ─────────────────
            await GotoAsync(page, $"/APInvoice?searched=true&searchSupplier={Supplier}&pageSize=100");
            await AssertVisibleInListAsync(page, grId,
                "步驟④：應付帳款列表應出現以該進貨單為來源的發票。" +
                "AP 認列時點是「貨實際進正常倉」（WarehouseApiController.Fulfillment），" +
                "沒出現代表上架沒有走到認列，或該料號不是免驗直放而被擋在待驗倉。");

            // ── ⑤ 付款：核銷該張發票 → 確認 ─────────────────────────────────────
            await GotoAsync(page, "/APPayment?searched=true");
            await page.ClickAsync("button[data-url$='/APPayment/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #panelForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.Locator("#sidePanelBody select[name='SupplierCode']").SelectOptionAsync(Supplier);
            await page.WaitForTimeoutAsync(1200);   // loadOpenInvoices 非同步載入未沖銷發票

            var invoiceRow = page.Locator("#invoiceContainer tr", new PageLocatorOptions { HasText = grId }).First;
            Assert.True(await invoiceRow.CountAsync() > 0,
                $"步驟⑤：付款單的未沖銷發票清單找不到來源為 {grId} 的發票。{DescribeDialogs()}");

            // 全額核銷：以該列自身的未沖銷金額為準，不寫死金額（稅率與幣別設定會影響總額）。
            var allocInput = invoiceRow.Locator(".alloc-amt").First;
            var outstanding = await allocInput.GetAttributeAsync("data-outstanding") ?? "0";
            await allocInput.FillAsync(outstanding);
            await page.WaitForTimeoutAsync(300);

            await page.SelectOptionAsync("#sidePanelBody select[name='BankAccountId']", Bank);
            await page.FillAsync("#sidePanelBody input[name='Amount']", outstanding);

            await SavePanelAsync(page, "#sidePanelBody button[type='submit']", "步驟⑤：付款單存檔");

            // 鎖定本次建立的那張付款單再按確認：列表上還有各 smoke test 留下的付款單，
            // 取「第一顆確認鈕」會確認到別人的單。以 searchStatus=Draft 排除歷次流程測試
            // 已確認的付款單（否則同一天重跑時，兩張日期相同的單排序不定）。
            await GotoAsync(page,
                $"/APPayment?searched=true&searchSupplier={Supplier}&searchStatus=Draft&pageSize=50");
            var paymentId = await ReadFirstRowDocIdAsync(page, "步驟⑤：付款單列表");

            var confirmBtn = page.Locator($"[data-action='confirmPayment'][data-id='{paymentId}']");
            Assert.True(await confirmBtn.CountAsync() > 0,
                $"步驟⑤：付款單 {paymentId} 沒有「確認」按鈕。確認鈕的渲染條件必須與服務層一致" +
                "（只擋已付款／已作廢）——若條件寫成某個從未被設定的狀態，付款單就永遠無法核銷。");
            await confirmBtn.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ⑥ 結果：該張應付發票已付款完畢 ──────────────────────────────────
            // 以 searchStatus 篩選而非比對畫面文字：狀態欄顯示的是在地化字串，
            // 查詢參數比對的是原始值，語系無關。
            await GotoAsync(page,
                $"/APInvoice?searched=true&searchSupplier={Supplier}&searchStatus=Paid&pageSize=100");
            await AssertVisibleInListAsync(page, grId,
                "步驟⑥：核銷確認後，該進貨單的應付發票應出現在 searchStatus=Paid 的查詢結果中。" +
                "沒出現代表付款確認沒有回寫發票的已付金額／狀態。");

            Assert.True(JsErrors.Count == 0,
                $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        /// <summary>採購單面板加一筆明細並填妥數量／單價（選品號會觸發帶出採購單位）。</summary>
        private static async Task AddPurchaseLineAsync(IPage page)
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
