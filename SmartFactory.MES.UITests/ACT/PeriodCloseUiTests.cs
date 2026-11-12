using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 月結 → 關帳 → 期間鎖 → 重開，從畫面完整走一次。
    ///
    /// <para><see cref="MonthEndUiTests"/> 刻意不做任何成功的狀態變更（它可能跑在共用的 :5110），
    /// 所以十個步驟、StartClosing、ClosePeriod、Reopen 的成功路徑在 UI 層一直是零。
    /// 本測試只動種子 ㉑ 建立的 <b>2090-01</b> 期間——E2E 專屬、遠在任何真實日期之後，
    /// 關帳不會擋到任何其他測試或真實資料的過帳；結束時（含失敗）一律還原成 Open。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class PeriodCloseUiTests
    {
        private readonly UiTestFixture _fixture;
        public PeriodCloseUiTests(UiTestFixture fixture) => _fixture = fixture;

        private const string Period    = "209001";
        private const string Checklist = "209001-P1";

        private static Task<string?> PeriodStatusAsync() =>
            SeedFixture.ScalarAsync<string>(
                $"""SELECT "Status"::text FROM "AccountingPeriods" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'""");

        /// <summary>按下月結頁上的按鈕，輪詢直到條件成立或出現紅色錯誤 toast；成功後重新載入頁面。</summary>
        private async Task ClickAndWaitAsync(IPage page, string selector, string doneSql, string what)
        {
            await page.ClickAsync(selector);
            for (var waited = 0; waited < 15_000; waited += 250)
            {
                if (await SeedFixture.ScalarAsync<long>(doneSql) > 0)
                {
                    await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd?periodId={Period}",
                        new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                    return;
                }
                if (await page.Locator("#errorToast.show").CountAsync() > 0)
                    Assert.Fail($"{what} 失敗：{await page.InnerTextAsync("#errorMsg")}");
                await Task.Delay(250);
            }
            Assert.Fail($"{what}：15 秒內資料庫沒有變化。");
        }

        [Fact]
        public async Task 月結十步走完即關帳_關帳後不能過帳_重開後恢復()
        {
            await SeedFixture.ResetAsync();
            try
            {
                var page = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(page);
                await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd?periodId={Period}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

                // ① 初始化清單
                await ClickAndWaitAsync(page, "[data-action='initChecklist']",
                    $"""SELECT count(*) FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}'""", "初始化月結清單");
                Assert.Equal(10L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}'"""));

                // ② 依序完成十步；第 8 步推進 Closing、第 10 步關帳
                for (var step = 1; step <= 10; step++)
                {
                    // 步驟 4 要真的執行實際費率重估（就像操作員會做的），才會替在製工單建本期 WIP 快照。
                    // 原本只按「完成」——那是鎖定不是重估——能過只是因為測試庫剛好沒有在製工單；
                    // 其他流程測試（例如 ContainerWipFlowTests）留下一張生產中工單，步驟 10 的關帳前
                    // 稽核就以 WIP_SNAPSHOT_MISSING 擋下。稽核擋得對，該補的是操作步驟。
                    if (step == 4)
                    {
                        await page.ClickAsync("button[data-action='runActualRateReest'][data-step-no='4']");
                        await Assertions.Expect(page.Locator("#deptCostModal.show")).ToBeVisibleAsync();
                        await page.FillAsync("#deptCostInput", "E2E-WC01:300:150");   // 種子值；SeedFixture.ResetAsync 會清掉本期快照與傳票
                        var reest = await page.RunAndWaitForResponseAsync(
                            () => page.ClickAsync("#btnConfirmDeptCost"),
                            r => r.Url.Contains("/MonthEnd/RunActualRateReestimation"));
                        var body = await reest.TextAsync();
                        Assert.True(body.Contains("\"success\":true"), $"步驟 4 實際費率重估失敗：{body}");
                        var inProgress = await SeedFixture.ScalarAsync<long>(
                            """SELECT count(*) FROM "WorkOrders" WHERE "PlantId"='P1' AND "Status"=2""");
                        Assert.Equal(inProgress, await SeedFixture.ScalarAsync<long>(
                            $"""SELECT count(*) FROM "WipSnapshots" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'"""));
                    }

                    await ClickAndWaitAsync(page,
                        $"button[data-action='completeStep'][data-step-no='{step}']",
                        $"""SELECT count(*) FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}' AND "StepNo"={step} AND "Status"=2""",
                        $"月結步驟 {step}");

                    if (step == 7) Assert.Equal("0", await PeriodStatusAsync());   // 還沒封鎖
                    if (step == 8) Assert.Equal("1", await PeriodStatusAsync());   // Closing
                }

                Assert.Equal("2", await PeriodStatusAsync());                      // Closed
                Assert.Equal("true", await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "IsCompleted"::text FROM "MonthEndChecklists" WHERE "ChecklistId"='{Checklist}'"""));

                // ③ 已關帳的期間不能過帳：從手動傳票畫面開一張 2090-01-20 的傳票，必須被擋、而且要說得出原因。
                await AssertManualJournalRejectedAsync(page);

                // ④ 會計期間畫面重開（Closed → Reopened）
                var alerts = new List<string>();
                page.Dialog += async (_, d) =>
                {
                    if (d.Type == DialogType.Alert) alerts.Add(d.Message);
                    try { await d.AcceptAsync(); } catch (PlaywrightException) { }
                };
                await page.GotoAsync($"{_fixture.BaseUrl}/AccountingPeriod?searched=true&year=2090",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await page.ClickAsync($"[data-action='reopenPeriod'][data-id='{Period}']");
                for (var waited = 0; waited < 10_000 && await PeriodStatusAsync() != "3" && alerts.Count == 0; waited += 250)
                    await Task.Delay(250);
                Assert.True(alerts.Count == 0, $"重開失敗：{string.Join(" / ", alerts)}");
                Assert.Equal("3", await PeriodStatusAsync());                      // Reopened
            }
            finally
            {
                // 不論成敗都回到 Open：部門實際成本等測試會選到這個期間，停在 Closed 會連帶讓它們紅。
                await SeedFixture.ScalarAsync<long>(
                    $"""UPDATE "AccountingPeriods" SET "Status"=0, "ClosedAt"=NULL, "ClosedBy"=NULL WHERE "PeriodId"='{Period}' AND "PlantId"='P1' RETURNING 1""");
            }
        }

        private async Task AssertManualJournalRejectedAsync(IPage page)
        {
            const string countSql = """SELECT count(*) FROM "JournalEntries" WHERE "PeriodId"='209001' AND "PlantId"='P1'""";
            var before = await SeedFixture.ScalarAsync<long>(countSql);

            var alerts = new List<string>();
            void OnDialog(object? _, IDialog d)
            {
                alerts.Add(d.Message);
                _ = d.AcceptAsync();
            }
            page.Dialog += OnDialog;
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/JournalEntry?searched=true",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await page.ClickAsync("button[data-url$='/JournalEntry/Create']");
                await page.WaitForSelectorAsync("#sidePanelBody #manualJournalForm");

                await page.FillAsync("#sidePanelBody input[name='EntryDate']", "2090-01-20");
                await page.FillAsync("#sidePanelBody input[name='Description']", "E2E 關帳後過帳（應被擋）");
                await page.Locator("#sidePanelBody select[name='Lines[0].AccountCode']").SelectOptionAsync("E2E-6101");
                await page.FillAsync("#sidePanelBody input[name='Lines[0].Debit']", "50");
                await page.Locator("#sidePanelBody select[name='Lines[1].AccountCode']").SelectOptionAsync("E2E-1101");
                await page.FillAsync("#sidePanelBody input[name='Lines[1].Credit']", "50");
                await page.ClickAsync("#sidePanelBody button[type='submit']");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1500);

                Assert.Equal(before, await SeedFixture.ScalarAsync<long>(countSql));

                // 失敗要有訊息：JSON 失敗走 alert，驗證失敗重繪面板帶訊息。兩者都沒有＝靜默失敗。
                var panelErrors = await page.Locator(
                    "#sidePanelBody .validation-summary-errors, #sidePanelBody .text-danger:not(:empty), #sidePanelBody .alert-danger")
                    .AllInnerTextsAsync();
                var message = string.Join(" / ", alerts.Concat(panelErrors).Where(s => !string.IsNullOrWhiteSpace(s)));
                Assert.False(string.IsNullOrWhiteSpace(message), "過帳到已關帳期間被擋下了，但畫面上沒有任何說明。");
            }
            finally
            {
                page.Dialog -= OnDialog;
            }
        }
    }
}
