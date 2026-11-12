using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 採購分攤費用單（LandedCostAllocation / LCA）面板動作守門。
    ///
    /// 單號採番、列表新單在前，沿用 base 預設「開列表第一列」。Create 存為草稿，
    /// 表頭 DocDate（預填）/Basis（預設 Amount）必填、TotalLandedCost 填一個值即可；
    /// 明細**至少一筆**是 Save 的硬性守門（Lines.Count==0 直接擋回），且明細只能靠
    /// GR 選入器（modal）加入——openGrPicker 會先把 modal 搬到 document.body，避免
    /// 在 offcanvas 內灰屏凍結，故 modal 內元素一律以 page 層（非面板層）定位。
    ///
    /// E2E 種子補了一張 Confirmed 進貨單 E2E-PE01 + 明細（E2E-FG01），讓
    /// GoodsReceiptLines 選入器有可勾選的列（乾淨庫一張進貨單都沒有）。
    /// </summary>
    public class LandedCostAllocationActionSmokeTests : DocActionSmokeTestBase
    {
        public LandedCostAllocationActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/LandedCostAllocation";
        protected override string NewButtonSelector  => "button[data-url$='/LandedCostAllocation/Create']";
        protected override string PanelReadySelector => "#lcaForm";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        // ⑤：乾淨庫沒有分攤單的簽核設定，存檔即核准，面板直接出現「確認」。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            new DocActionStep("Confirm", ExpectedStatus: "1"),   // DocStatus.Confirmed
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "LandedCostAllocations" WHERE "LandedCostAllocationId" = '{0}'""";

        protected override bool DraftDeletableFromList => true;

        /// <summary>
        /// 確認的結果不只是狀態：100 元要真的分到明細上，而且要過一張借貸平衡、
        /// 貸方 100 的分錄（借 存貨／進料費用差異、貸 暫估應付運費）。
        /// 狀態變了但分錄沒過，是這類動作最典型的半套失敗。
        /// </summary>
        protected override async Task AssertAfterActionsAsync(string docId)
        {
            var id = docId.Replace("'", "''");

            var allocated = await SeedFixture.ScalarAsync<decimal>(
                $"""SELECT COALESCE(SUM("AllocatedCost"),0) FROM "LandedCostAllocationLines" WHERE "LandedCostAllocationId" = '{id}'""");
            Assert.True(allocated == 100m, $"{docId}：確認後明細分攤合計應為 100，實際 {allocated}。");

            const string lines = """
                FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId" = l."JournalId"
                WHERE e."SourceDocType" = 'LandedCostAllocation' AND e."SourceDocId" = '{0}'
                """;
            var credit = await SeedFixture.ScalarAsync<decimal>($"SELECT COALESCE(SUM(l.\"Credit\"),0) {string.Format(lines, id)}");
            var debit  = await SeedFixture.ScalarAsync<decimal>($"SELECT COALESCE(SUM(l.\"Debit\"),0) {string.Format(lines, id)}");
            Assert.True(credit == 100m, $"{docId}：確認後應過一張貸方 100 的分錄，實際貸方 {credit}。");
            Assert.True(debit == credit, $"{docId}：分錄借貸不平衡（借 {debit}／貸 {credit}）。");
        }

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 待分攤費用總額：帶個正值（非空表頭欄位，避免 0 元的分攤單看起來像沒填完）。
            await page.FillAsync($"{PanelBodySel} #totalLandedCost", "100");

            // 開 GR 選入器（modal 會被搬到 document.body，故其內元素以 page 層定位）。
            await page.ClickAsync($"{PanelBodySel} [data-action='openGrPicker']");

            // 鎖定種子那張進貨單再勾：選入器依單號倒序，流程測試建的 RC- 進貨單排在 E2E- 前面，
            // 不搜尋的話勾到哪張取決於測試執行順序（9/30 實測勾到的是 RC-2609-00009）。
            // 打開時會先跑一次不帶條件的 loadGrLines；等它回來再搜尋，否則它晚到會蓋掉搜尋結果。
            await page.WaitForSelectorAsync("#grPickerBody .gr-pick",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.FillAsync("#grPickerTerm", "E2E-PE01");
            await page.ClickAsync("[data-action='loadGrLines']");
            await page.WaitForFunctionAsync(@"() => {
                const rows = [...document.querySelectorAll('#grPickerBody tr')];
                return rows.length > 0 && rows.every(r => r.innerText.includes('E2E-PE01'));
            }", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
            await page.CheckAsync("#grPickerBody .gr-pick >> nth=0");

            // 加入所選 → 明細列進 #linesTbody、modal 關閉。
            // 明細帶的是 type=hidden 的 input，永遠不會「visible」，故等 Attached 即可。
            await page.ClickAsync("[data-action='addSelectedGrLines']");
            await page.WaitForSelectorAsync("#linesTbody input[name$='.GoodsReceiptId']",
                new PageWaitForSelectorOptions { Timeout = 10_000, State = WaitForSelectorState.Attached });
            await page.WaitForTimeoutAsync(300);
        }
    }
}
