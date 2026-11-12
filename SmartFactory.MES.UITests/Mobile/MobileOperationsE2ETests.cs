using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機現場作業頁走真後端：上工／下工、送審中工單開工被擋、機台狀態查詢、排程查詢、生產準備。
    ///
    /// <para><b>為什麼要補</b>：2026-10-20 盤點時這幾頁只有「腳本載入不出錯」的冒煙測試
    /// （MobilePageScriptSmokeTests），按下按鈕之後的路徑——API 回應、toast、資料庫寫入——沒有任何
    /// Playwright 走過。API 本身有整合測試，但頁面把錯誤吞掉、按鈕接錯 data-action、或回應欄位改名
    /// 讓畫面靜默空白，只有走畫面才看得到。</para>
    ///
    /// <para>動到種子資料的測試在 finally 呼叫 <see cref="SeedFixture.ResetAsync"/> 還原 E2E 資料。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileOperationsE2ETests
    {
        private const string Wo = "E2E-WO0021";
        private const string Dispatch = "E2E-DP0025";   // 已投產＋已核准工單的已排程派工，機台 E2E-MC02

        private readonly UiTestFixture _fixture;
        public MobileOperationsE2ETests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<int> CountAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : 0;

        // ── 上工頁：開工 → 完工，資料庫有一筆起訖都有的上工紀錄 ───────────────────

        [Fact]
        public async Task Sessions_StartThenComplete_WritesSessionWithStartAndEnd()
        {
            await SeedFixture.ExecAsync($"""DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'""");
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/sessions.html?dispatchId={Dispatch}");
                var start = page.Locator("#btnStart");
                await Assertions.Expect(start).ToBeVisibleAsync(new() { Timeout = 15_000 });

                var (startToast, _) = await MobileUi.ToastAfterAsync(page, () => start.ClickAsync());
                Assert.True(await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}' AND "EndAt" IS NULL""") == 1,
                    $"開工後應有一筆進行中的上工紀錄（toast：{startToast}）");
                await Assertions.Expect(page.Locator("#btnStop")).ToBeVisibleAsync();

                var (stopToast, _) = await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnStop").ClickAsync());
                var ended = 0;
                for (var i = 0; i < 20 && ended == 0; i++)
                {
                    ended = await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}' AND "EndAt" IS NOT NULL""");
                    if (ended == 0) await page.WaitForTimeoutAsync(300);
                }
                Assert.True(ended == 1, $"完工後上工紀錄應有結束時間（toast：{stopToast}）");
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync($"""DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'""");
                await SeedFixture.ResetAsync();
            }
        }

        // ── 送審中的工單：手機開工被擋，toast 寫出原因，不建上工紀錄 ────────────────

        [Fact]
        public async Task Sessions_Start_WorkOrderPendingApproval_ToastShowsReason()
        {
            await SeedFixture.ExecAsync($"""
                DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}';
                UPDATE "WorkOrders" SET "ApprovalStatus"=1 WHERE "WorkOrderId"='{Wo}';
                """);
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/sessions.html?dispatchId={Dispatch}");
                var start = page.Locator("#btnStart");
                await Assertions.Expect(start).ToBeVisibleAsync(new() { Timeout = 15_000 });

                var (text, success) = await MobileUi.ToastAfterAsync(page, () => start.ClickAsync());

                Assert.False(success, $"送審中的工單不該開工成功：{text}");
                Assert.Contains("尚未核准", text);
                Assert.Equal(0, await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ResetAsync();
            }
        }

        // ── 機台狀態：網址帶機台代號即查出狀態與可用的異動 ───────────────────────────

        [Fact]
        public async Task MachineStatus_LoadsMachine_AndOffersEvents()
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/machine-status.html?machine=E2E-MC01");
                await Assertions.Expect(page.Locator("#detailSection")).ToBeVisibleAsync(new() { Timeout = 15_000 });
                await Assertions.Expect(page.Locator("#machineCode")).ToContainTextAsync("E2E-MC01");
                Assert.True(await page.Locator("#eventButtons button").CountAsync() > 0, "機台查得到卻沒有任何可選的狀態異動");
                Assert.Equal(0, await page.Locator("#toast-container .toast.text-bg-danger").CountAsync());
            }
            finally { await ctx.CloseAsync(); }
        }

        // ── 排程查詢：以工單號查得到它的派工（卡片顯示工單、工序、機台與時段，不顯示派工單號） ──

        [Fact]
        public async Task ScheduleQuery_ByWorkOrder_ListsItsDispatches()
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/schedule-query.html");
                await page.FillAsync("#inputWo", Wo);
                var resp = page.WaitForResponseAsync(r => r.Url.Contains("/api/mobile/schedule-query"));
                await page.ClickAsync("[data-action='doSearch']");
                Assert.Equal(200, (await resp).Status);
                var result = page.Locator("#resultContent");
                await Assertions.Expect(result).ToContainTextAsync(Wo, new() { Timeout = 10_000 });
                await Assertions.Expect(result).ToContainTextAsync("E2E-MC0");
            }
            finally { await ctx.CloseAsync(); }
        }

        // ── 生產準備：切換今天／明天都拿得到資料，不跳錯誤 ──────────────────────────

        [Fact]
        public async Task Preparation_TodayAndTomorrow_LoadWithoutError()
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/preparation.html");
                foreach (var btn in new[] { "#btnToday", "#btnTomorrow" })
                {
                    var resp = page.WaitForResponseAsync(r => r.Url.Contains("/api/mfg/preparation"));
                    await page.ClickAsync(btn);
                    Assert.Equal(200, (await resp).Status);
                    await page.WaitForFunctionAsync(
                        "() => !document.getElementById('prepContent').classList.contains('d-none') && document.getElementById('prepContent').innerHTML.trim().length > 0"
                        + " || !document.getElementById('emptyState').classList.contains('d-none')",
                        null, new() { Timeout = 10_000 });
                }
                Assert.Equal(0, await page.Locator("#toast-container .toast.text-bg-danger").CountAsync());
            }
            finally { await ctx.CloseAsync(); }
        }
    }
}
