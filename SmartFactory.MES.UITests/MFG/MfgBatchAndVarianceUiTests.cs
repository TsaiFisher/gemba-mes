using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 生產批次（調整優先序、整批投產）與 MRP 差異決策（結案退場、標記已處理）的畫面接線。
    ///
    /// <para>這兩頁之前只有 MenuWalk 的「打得開」。生產批次明細的兩顆按鈕曾經全是死的——
    /// 原本寫 <c>typeof postAction === 'function'</c> 才送出，而 postAction 從沒掛到 window（見 _Details 的註解）；
    /// 修好之後沒有任何測試點過，同一種寫法在工單明細又出現了一次（DispatchExceptionUiTests 守的那個撤回）。</para>
    ///
    /// <para>資料：scripts/seed-e2e-fixture.sql ⑱——批次 E2E-BATCH02（工單 WO0041／WO0042）、
    /// 請購單 E2E-PRV01 與差異紀錄 E2E-VAR01～03（MrpRunId=E2E-MRPV）。</para>
    /// </summary>
    public class MfgBatchAndVarianceUiTests : FlowTestBase
    {
        public MfgBatchAndVarianceUiTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 生產批次_明細面板調整優先序_寫回批次()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await OpenBatchPanelAsync(page);

            await page.FillAsync("#sidePanelBody #batchPriority", "7");
            await ClickAndWaitPostAsync(page, "#sidePanelBody [data-action='updateBatchPriority']", "/ProductionBatch/UpdatePriority");

            Assert.Equal(7m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT \"Priority\" FROM \"ProductionBatches\" WHERE \"BatchId\" = 'E2E-BATCH02'"));
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task 生產批次_整批投產_批內已規劃工單全部釋出()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await OpenBatchPanelAsync(page);

            await ClickAndWaitPostAsync(page, "#sidePanelBody [data-action='releaseBatch']", "/ProductionBatch/ReleaseBatch");

            Assert.Equal(2L, await SeedFixture.ScalarAsync<long>(
                "SELECT count(*) FROM \"WorkOrders\" WHERE \"ProductionBatchId\" = 'E2E-BATCH02' AND \"Status\" = 1"));
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}{DescribeDialogs()}");
        }

        [Fact]
        public async Task MRP差異決策_勾選結案_已無需求的請購明細結案退場_數量變動類略過維持未結()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await GotoAsync(page, "/MrpVarianceDecision?searched=true&onlyOpen=true&searchMrpRunId=E2E-MRPV&pageSize=50");

            await CheckVarianceAsync(page, "E2E-VAR01");
            await CheckVarianceAsync(page, "E2E-VAR02");
            await ClickAndWaitPostAsync(page, "[data-action='closeSelectedVariances']", "/MrpVarianceDecision/CloseLines");

            Assert.Equal(1, await VarianceStatusAsync("E2E-VAR01"));
            Assert.True(await LineClosedAsync(1), "VAR01（已無需求）結案後，請購 E2E-PRV01 第 1 行應為 IsClosed。");

            // 數量變動不能用結案處理：差異維持未結、請購明細不動，並在回應訊息說明略過原因
            Assert.Equal(0, await VarianceStatusAsync("E2E-VAR02"));
            Assert.False(await LineClosedAsync(2), "VAR02（需求增加）不可被結案，請購第 2 行卻被關了。");
            Assert.Contains(DialogMessages, m => m.Contains("E2E-RM02"));
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task MRP差異決策_勾選標記已處理_只結清差異不動請購單()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await GotoAsync(page, "/MrpVarianceDecision?searched=true&onlyOpen=true&searchMrpRunId=E2E-MRPV&pageSize=50");

            await CheckVarianceAsync(page, "E2E-VAR03");
            await ClickAndWaitPostAsync(page, "[data-action='ackSelectedVariances']", "/MrpVarianceDecision/Acknowledge");

            Assert.Equal(1, await VarianceStatusAsync("E2E-VAR03"));
            Assert.False(await LineClosedAsync(3), "「已處理」只標記差異，不得動到請購明細。");
            Assert.Equal(0, await VarianceStatusAsync("E2E-VAR01"));   // 沒勾的不受影響

            // 已結清的不再出現在「只列未結」的清單上
            await GotoAsync(page, "/MrpVarianceDecision?searched=true&onlyOpen=true&searchMrpRunId=E2E-MRPV&pageSize=50");
            Assert.Equal(0, await page.Locator(".js-variance-check[value='E2E-VAR03']").CountAsync());
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════

        private async Task OpenBatchPanelAsync(IPage page)
        {
            await GotoAsync(page, "/ProductionBatch?searched=true&searchText=E2E-BATCH02&pageSize=50");
            await OpenRowPanelAsync(page, "E2E-BATCH02", "/ProductionBatch/Details", "#sidePanelBody [data-action='releaseBatch']");
        }

        private async Task CheckVarianceAsync(IPage page, string varianceId)
        {
            var box = page.Locator($".js-variance-check[value='{varianceId}']");
            Assert.True(await box.CountAsync() == 1, $"差異決策清單上找不到 {varianceId}。");
            await box.CheckAsync();
        }

        /// <summary>按鈕走 fetch（確認框由基底自動接受）；按下去必須真的發出 POST，否則就是「按了沒反應」。</summary>
        private async Task ClickAndWaitPostAsync(IPage page, string selector, string urlFragment)
        {
            IResponse response;
            try
            {
                response = await page.RunAndWaitForResponseAsync(
                    () => page.ClickAsync(selector),
                    r => r.Url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase) && r.Request.Method == "POST",
                    new PageRunAndWaitForResponseOptions { Timeout = 15_000 });
            }
            catch (TimeoutException)
            {
                Assert.Fail($"按下 {selector} 後沒有發出 POST {urlFragment}——按鈕沒有作用。{DescribeDialogs()}");
                throw;
            }
            var body = await response.TextAsync();
            Assert.True(response.Ok && body.Replace(" ", "").Contains("\"success\":true"),
                $"POST {urlFragment} 失敗（HTTP {response.Status}）：{body}{DescribeDialogs()}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1200);
        }

        private static async Task<int> VarianceStatusAsync(string id)
            => await SeedFixture.ScalarAsync<int>(
                $"SELECT \"Status\" FROM \"MrpPurchaseVarianceRecords\" WHERE \"VarianceId\" = '{id}'");

        private static async Task<bool> LineClosedAsync(int lineNo)
            => await SeedFixture.ScalarAsync<bool>(
                $"SELECT \"IsClosed\" FROM \"PurchaseRequestLines\" WHERE \"PurchaseRequestId\" = 'E2E-PRV01' AND \"LineNo\" = {lineNo}");
    }
}
