using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 第一條主幹流程：派工備料 → 確認鎖料 → 揀料移入備料倉 → 備料計時 → 開工 → 報工 → 完工 → 完工入庫。
    ///
    /// 為什麼寫成單一測試方法：每一步都以前一步的單據狀態為前提，xUnit 不保證方法執行順序，
    /// 拆成多個 [Fact] 只會得到隨機紅燈。步驟以註解分段，失敗訊息帶上該步驟名稱。
    ///
    /// 資料來源為 scripts/seed-e2e-fixture.sql 的 E2E-DP0001（派工，Scheduled）／
    /// E2E-WO0001（工單，Released），用料 E2E-RM01 ×20、E2E-RM02 ×10，
    /// 庫存在 E2E-WRAW/E2E-LRAW，工作中心 E2E-WC01 的預設備料倉為 E2E-WSTG/E2E-LSTG。
    /// 測試開頭會重跑該腳本把狀態歸零，因此可以重複執行。
    /// </summary>
    public class StagingToCompletionFlowTests : FlowTestBase
    {
        private const string DispatchId  = "E2E-DP0001";
        private const string WorkOrderId = "E2E-WO0001";
        private const string SourceWh    = "E2E-WRAW";
        private const string SourceLoc   = "E2E-LRAW";
        private const string StagingWh   = "E2E-WSTG";
        private const string FinishedWh  = "E2E-WFG";
        private const string FinishedLoc = "E2E-LFG";
        private const string FinishedProduct = "E2E-FG01";

        private static Task<int> WorkOrderStatusAsync() => SeedFixture.ScalarAsync<int>(
            $@"SELECT ""Status"" FROM ""WorkOrders"" WHERE ""WorkOrderId"" = '{WorkOrderId}'");

        public StagingToCompletionFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task Staging_Setup_Start_Report_Complete_FullFlow()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            // ── ① 派工 → 備料面板 → 建立備料單 ──────────────────────────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await AssertVisibleInListAsync(page, DispatchId, "步驟①：派工列表");

            await OpenRowPanelAsync(page, DispatchId, "SetupForm", "#stagingForm");

            // 模具已改為沿用排程指定值，備料面板不該再有模具輸入欄（dea44a8 的行為變更）
            Assert.Equal(0, await page.Locator("input[name='toolingCode']").CountAsync());

            await page.Locator("#stagingForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ② 備料單：填揀料來源儲位並儲存 ──────────────────────────────
            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={DispatchId}&pageSize=50");
            await AssertVisibleInListAsync(page, DispatchId,
                "步驟②：備料單列表（派工的「建立備料單」應已產生一張以該派工為來源的單）");

            await OpenRowPanelAsync(page, DispatchId, "/MaterialStaging/Edit", "#linesBody tr");

            var lineCount = await page.Locator("#linesBody tr").CountAsync();
            Assert.True(lineCount >= 2,
                $"步驟②：備料單應帶入 2 筆用料（RM01/RM02），實際 {lineCount} 筆。" +
                "帶料來源為派工的 DispatchMaterials，請確認種子的用料明細。");

            // 備料倉應由工作中心的預設值自動帶入
            Assert.Equal(StagingWh, await page.Locator("#stagingWh").InputValueAsync());

            for (var i = 0; i < lineCount; i++)
            {
                var row = page.Locator("#linesBody tr").Nth(i);
                await row.Locator(".line-from-wh").SelectOptionAsync(SourceWh);
                await page.WaitForTimeoutAsync(200);   // 倉庫→儲位連動是同步 JS，僅需讓事件跑完
                await row.Locator(".line-from-loc").SelectOptionAsync(SourceLoc);
            }

            await page.Locator("#stagingForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ③ 送審（無簽核流程設定時直接核准）→ 確認鎖料 ────────────────
            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "/MaterialStaging/Edit", "#linesBody tr");

            // Confirm 要求 ApprovalStatus=Approved。以「確認」按鈕在不在來判斷是否還要送審，
            // 而不是看「送審」按鈕在不在——由派工建立的備料單已經是核准狀態，此時兩顆按鈕
            // 可能同時存在，硬送審只會拿到「僅草稿或退回狀態可送審」的錯誤。
            // 有簽核流程設定的部署則會落在這個分支：送審 →（無流程時直接核准）。
            if (await page.Locator("[data-url*='/MaterialStaging/Confirm']").CountAsync() == 0)
            {
                await ClickPanelActionAsync(page, "/MaterialStaging/Submit");
                await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={DispatchId}&pageSize=50");
                await OpenRowPanelAsync(page, DispatchId, "/MaterialStaging/Edit", "#linesBody tr");
            }

            Assert.True(await page.Locator("[data-url*='/MaterialStaging/Confirm']").CountAsync() > 0,
                "步驟③：找不到「確認」按鈕。多半是備料單還沒核准（Confirm 要求 Approved）。");
            await ClickPanelActionAsync(page, "/MaterialStaging/Confirm");

            // ── ④ 揀料完成 → 實際移入備料倉 ────────────────────────────────
            await GotoAsync(page, $"/MaterialStaging?searched=true&searchTerm={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "/MaterialStaging/Edit", "#linesBody tr");

            Assert.True(await page.Locator("[data-url*='/MaterialStaging/Stage']").CountAsync() > 0,
                "步驟④：找不到「揀料完成」按鈕，代表上一步的確認沒有成立（單據應為 Confirmed）。");
            await ClickPanelActionAsync(page, "/MaterialStaging/Stage");

            // 以 status 篩選而非比對畫面文字：狀態欄顯示的是在地化字串（「已完成」），
            // 而 status 查詢參數比對的是原始值，語系無關。
            await GotoAsync(page,
                $"/MaterialStaging?searched=true&searchTerm={DispatchId}&status=Completed&pageSize=50");
            await AssertVisibleInListAsync(page, DispatchId,
                "步驟④：揀料完成後，備料單應出現在 status=Completed 的查詢結果中");

            // ── ⑤ 備料計時：開始 → 完成 ────────────────────────────────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "SetupForm", "#panelForm");
            await page.Locator("#panelForm button[value='start']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "SetupForm", "#panelForm");
            await page.Locator("#panelForm button[value='done']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ⑥ 開工 ─────────────────────────────────────────────────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "SessionForm", "#panelForm");
            await page.Locator("#panelForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ⑦ 報工 ×2（第二次帶不良品，驗不良原因欄位自動顯示）──────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "ReportForm", "#panelForm");
            await page.FillAsync("input[name='qtyOk']", "4");
            await page.Locator("#panelForm button[value='report']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "ReportForm", "#panelForm");
            await page.FillAsync("input[name='qtyOk']", "4");
            await page.FillAsync("input[name='qtyNg']", "1");
            await page.WaitForTimeoutAsync(400);
            Assert.True(await page.Locator("#ngReasonBlock").IsVisibleAsync(),
                "步驟⑦：填入不良數後，不良原因欄位應自動顯示");
            await page.Locator("#panelForm button[value='report']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // ── ⑧ 完工（帶最後一批數量）────────────────────────────────────
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={DispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, DispatchId, "ReportForm", "#panelForm");
            await page.FillAsync("input[name='qtyOk']", "2");
            await page.Locator("#panelForm button[value='complete']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(2000);

            // ── ⑨ 結果：工單轉為已完工（待入庫）─────────────────────────────
            // 原本只驗「列表上找得到這張工單」，那在任何狀態下都成立——完工沒生效也照樣綠。
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={WorkOrderId}&pageSize=50");
            await AssertVisibleInListAsync(page, WorkOrderId, "步驟⑨：工單列表");
            Assert.True(await WorkOrderStatusAsync() == 3,
                $"步驟⑨：派工完工後工單應為 Completed(3)，實際為 {await WorkOrderStatusAsync()}。" +
                "工單狀態由派工完工同步，同步沒跑的話完工入庫畫面根本不會列出這張單。");

            // ── ⑩ 完工入庫：選成品倉儲位確認 → 工單結案、成品入庫 ─────────────
            // 這一步之前沒有任何 UI 測試走到：完工入庫是成品進帳、原料後沖、成本結轉的同一個交易，
            // 卻只有服務層測試在守。
            await GotoAsync(page, $"/ProductionCompletion?searched=true&searchText={WorkOrderId}");
            await AssertVisibleInListAsync(page, WorkOrderId, "步驟⑩：完工入庫列表（只列 Completed 工單）");
            await OpenRowPanelAsync(page, WorkOrderId, "ConfirmForm", "#panelForm");
            await page.Locator("#whSelect").SelectOptionAsync(FinishedWh);
            await page.FillAsync("#locationInput", FinishedLoc);

            var completion = await page.RunAndWaitForResponseAsync(
                () => page.Locator("#panelForm button[value='complete']").ClickAsync(),
                r => r.Url.Contains($"/ProductionCompletion/Complete"));
            var completionBody = await completion.TextAsync();
            Assert.True(completionBody.Contains("\"success\":true"),
                $"步驟⑩：完工入庫被拒：{completionBody}");

            Assert.True(await WorkOrderStatusAsync() == 4,
                $"步驟⑩：入庫後工單應為 Closed(4)，實際為 {await WorkOrderStatusAsync()}。");
            var fgIn = await SeedFixture.ScalarAsync<decimal>($@"
                SELECT coalesce(sum(""StockQty""),0) FROM ""StockMovements""
                WHERE ""ProductCode"" = '{FinishedProduct}' AND ""Direction"" = 1 AND ""LocationCode"" = '{FinishedLoc}'");
            Assert.True(fgIn > 0m,
                $"步驟⑩：{FinishedProduct} 在 {FinishedLoc} 沒有任何入庫異動——API 回了成功，帳卻沒有進。");

            // 整條流程不應有任何 JS 錯誤
            Assert.True(JsErrors.Count == 0,
                $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }
    }
}
