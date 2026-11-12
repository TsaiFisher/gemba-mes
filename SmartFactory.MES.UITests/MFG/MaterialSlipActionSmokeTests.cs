using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 領／備料單（MaterialSlip）「已存在單據」面板動作守門。
    ///
    /// MFG 家族第一支 <see cref="DocActionSmokeTestBase"/>——MFG 的核心單據（工單／派工）
    /// 多為系統自動產生，沒有手動 Create 面板；MaterialSlip 是少數走「列表 → 開新面板 →
    /// Save → 重開」標準流程的手動單據，正好套這套 scaffold。
    ///
    /// 它的明細 scaffold 是自己寫的（<c>data-action="addMsLine"</c> / <c>#msLinesBody</c>，
    /// 非 INV 共用的 addLine / #linesTbody），所以直接繼承 <see cref="DocActionSmokeTestBase"/>
    /// 而不是 InvLineDocActionSmokeTestBase，填表邏輯自帶。
    ///
    /// 類別預設 InternalIssue（不需供應商）；Save 只建草稿、不過帳庫存，故無需基準庫存，
    /// 但仍重跑種子確保品號主檔存在。存檔後停在 Draft → 重開面板仍有動作鈕與明細 handler。
    /// </summary>
    public class MaterialSlipActionSmokeTests : DocActionSmokeTestBase
    {
        public MaterialSlipActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/MaterialSlip";
        // 新增鈕：data-url="/MaterialSlip/Form?category=X"；?category 把它跟列上的
        // 編輯鈕（/MaterialSlip/Form/SLIPID）區分開。
        protected override string NewButtonSelector  => "button[data-url*='/MaterialSlip/Form?category']";
        protected override string PanelReadySelector => "#materialSlipForm";

        // 重開既有單據走列上的編輯鈕 /MaterialSlip/Form/{id}（僅 Draft 顯示）；
        // 結尾斜線讓它不會誤中結尾為 "?category" 的新增鈕。
        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { "/MaterialSlip/Form/" };

        // 存檔用的是委外發料（見 FillRequiredFieldsAsync），列表預設只列廠內發料——不帶類別就找不到剛建的單
        protected override string ReopenIndexQuery => "categoryFilter=OutsourceIssue";

        // 種子只重置 E2E 那組（含品號主檔）；Save 不碰庫存，這裡純粹確保品號選項非空。
        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // SlipDate 由 Form GET 帶今天，免填。
            //
            // 2026-10-12 起來源類別／單號是下拉（原本自由輸入，打錯一碼工單比對就落空）。
            // ① 預設類別（廠內發料）＋來源類別「工單」：只列還會發料的工單——已釋出的 E2E-WO0001 在、
            //    已完工的 E2E-WO9001 不在（發料類不列完工）。
            var srcDoc = page.Locator($"{PanelBodySel} #msSourceDoc");
            Assert.True(await srcDoc.Locator("option[value='E2E-WO0001']").CountAsync() == 1,
                "來源單號下拉沒有已釋出的工單 E2E-WO0001");
            Assert.True(await srcDoc.Locator("option[value='E2E-WO9001']").CountAsync() == 0,
                "發料類的來源單號不該列出已完工的工單 E2E-WO9001");

            // ② 存檔改走委外發料＋委外採購單（已確認的 E2E-OPO01）：工單來源的發料要工單已核准，
            //    種子的 E2E-WO0001 未核准，用它會被擋下而不是單純存檔。切類別會重新載入來源清單。
            await page.Locator($"{PanelBodySel} select[name='Category']").SelectOptionAsync("OutsourceIssue");
            await page.Locator($"{PanelBodySel} #msSourceType").SelectOptionAsync("OutsourcePurchaseOrder");
            await srcDoc.Locator("option[value='E2E-OPO01']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
            // select2 包住了原生 select，直接設值並觸發 change（select2 會同步顯示）
            await page.EvaluateAsync("() => jQuery('#msSourceDoc').val('E2E-OPO01').trigger('change')");

            await page.ClickAsync($"{PanelBodySel} [data-action='addMsLine']");
            await page.WaitForTimeoutAsync(300);

            var lastRow = page.Locator($"{PanelBodySel} #msLinesBody tr.ms-line-row").Last;
            await lastRow.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");

            // ③ 單位由品號帶出、而且只能是庫存單位（工單用料／額度／過帳都以庫存單位計）
            var uom = lastRow.Locator("select[name$='.UomCode']");
            await Assertions.Expect(uom).ToHaveValueAsync("E2E-EA", new() { Timeout = 10_000 });
            Assert.Equal(1, await uom.Locator("option").CountAsync());

            await lastRow.Locator("input[name$='.RequestedQty']").First.FillAsync("1");
        }
    }
}
