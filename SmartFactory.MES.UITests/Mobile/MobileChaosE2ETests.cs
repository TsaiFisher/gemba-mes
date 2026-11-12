using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 混沌邊界走真後端：伺服器真的把下工做完了，回應卻在路上斷掉。
    ///
    /// <para><see cref="MobileChaosTests"/> 用假伺服器驗手機端的判斷；這支補上另一半——真伺服器的
    /// clientId 去重（SyncLog）在「同一筆第二次送到」時確實只做一次，畫面上也不出現失敗。
    /// 用 Playwright 的 route：先 <c>FetchAsync</c> 讓請求真的送到伺服器、拿到回應，再 <c>AbortAsync</c>
    /// 不交給頁面——頁面看到的是連線中斷，伺服器那邊已經提交。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileChaosE2ETests
    {
        private const string Dispatch = "E2E-DP0025";   // 已投產＋已核准工單的已排程派工

        private readonly UiTestFixture _fixture;
        public MobileChaosE2ETests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<int> CountAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : 0;

        [Fact]
        public async Task 下工回應在路上遺失_手機排隊重送_伺服器只結束一次_畫面顯示已排隊而非失敗()
        {
            await SeedFixture.ExecAsync($"""DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'""");
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            var lostOnce = 0;
            var resent = 0;
            string? clientId = null;
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/sessions.html?dispatchId={Dispatch}");
                var start = page.Locator("#btnStart");
                await Assertions.Expect(start).ToBeVisibleAsync(new() { Timeout = 15_000 });
                await MobileUi.ToastAfterAsync(page, () => start.ClickAsync());
                Assert.Equal(1, await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}' AND "EndAt" IS NULL"""));

                // 第一次下工：伺服器處理完、回應丟掉
                await page.RouteAsync("**/api/mobile/sessions/*/complete", async route =>
                {
                    if (Interlocked.Exchange(ref lostOnce, 1) == 0)
                    {
                        clientId = System.Text.Json.JsonDocument.Parse(route.Request.PostData ?? "{}")
                            .RootElement.GetProperty("clientId").GetString();
                        await route.FetchAsync();                     // 真的送到伺服器、等它提交
                        await route.AbortAsync("connectionreset");     // 回應不交給頁面
                        return;
                    }
                    Interlocked.Increment(ref resent);
                    await route.ContinueAsync();
                });

                var (toast, _) = await MobileUi.ToastAfterAsync(page, () => page.Locator("#btnStop").ClickAsync());
                // 「已排隊、連線後自動送出」是黃色；紅色就是把它當失敗了，人會重按
                Assert.Equal(0, await page.Locator("#toast-container .toast.text-bg-danger").CountAsync());
                Assert.Contains("text-bg-warning", await page.Locator("#toast-container .toast").Last.GetAttributeAsync("class") ?? "");
                Assert.NotNull(clientId);
                Assert.Equal(1, await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}' AND "EndAt" IS NOT NULL"""));
                // 連線中斷的那一刻轉排隊，之後由佇列自己補送（頁面在送出成功後會重新整理）；
                // 這裡只在還有待送時推一把，不等每分鐘重試
                Assert.True(await PagePoll.UntilAsync(page, """
                    async () => {
                        await offlineQueue.ready();
                        const all = await offlineQueue.getAll();
                        if (all.some(r => r.status === 'pending')) offlineQueue.flush();
                        return all.every(r => r.status === 'sent');
                    }
                    """, 15_000), "重送後佇列應全部送出，而不是留著或標成失敗");
                Assert.True(resent >= 1, "沒有發生重送——本測試沒有驗到伺服器去重。");

                Assert.Equal(1, await CountAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'"""));
                Assert.Equal(1, await CountAsync($"""SELECT count(*)::text FROM "SyncLogs" WHERE "ClientId"='{clientId}'"""));
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync($"""DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'""");
                await SeedFixture.ResetAsync();
            }
        }
    }
}
