using Microsoft.Playwright;
using SmartFactory.MES.UITests.Acceptance;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 現場遙測走真後端：手機頁面上發生的事（腳本錯誤、API 很慢、斷線）要真的出現在資料庫裡，
    /// 而且「現場遙測」儀表板看得到這支手機。
    ///
    /// <para>不等每分鐘的定時送出：直接呼叫 <c>mesTelemetry.flush()</c>（telemetry.js 為測試與除錯留的入口）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MobileTelemetryE2ETests
    {
        private readonly UiTestFixture _fixture;
        public MobileTelemetryE2ETests(UiTestFixture fixture) => _fixture = fixture;

        private static async Task<int> CountAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : 0;

        [Fact]
        public async Task 頁面錯誤_慢API_斷線_都回報到伺服器_儀表板看得到這支手機()
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/home.html", new() { WaitUntil = WaitUntilState.NetworkIdle });
                var device = await page.EvaluateAsync<string>("() => localStorage.getItem('mesDeviceId')");
                Assert.False(string.IsNullOrEmpty(device), "telemetry.js 沒有載入（沒有產生裝置代號）");

                // 1. 腳本錯誤（非同步丟出，才會走到 window.onerror 而不是 evaluate 本身失敗）
                await page.EvaluateAsync("() => setTimeout(() => { throw new Error('telemetry-e2e-boom'); }, 0)");

                // 2. 慢 API：攔下一支讀取，延遲 3.5 秒才放行
                await page.RouteAsync("**/api/mobile/plants/current", async r => { await Task.Delay(3500); await r.ContinueAsync(); });
                await page.EvaluateAsync("() => fetch('/api/mobile/plants/current', { credentials: 'include' })");

                // 3. 斷線 1 秒
                await ctx.SetOfflineAsync(true);
                await page.WaitForTimeoutAsync(1000);
                await ctx.SetOfflineAsync(false);

                await page.EvaluateAsync("() => mesTelemetry.flush()");
                var where = $"\"DeviceId\"='{device}'";
                for (var i = 0; i < 30 && await CountAsync($"""SELECT count(*)::text FROM "ClientTelemetryEvents" WHERE {where} AND "Kind"='Offline'""") == 0; i++)
                {
                    await page.WaitForTimeoutAsync(300);
                    await page.EvaluateAsync("() => mesTelemetry.flush()");
                }

                Assert.Equal(1, await CountAsync($"""SELECT count(*)::text FROM "ClientTelemetryEvents" WHERE {where} AND "Kind"='Error' AND "Message" LIKE '%telemetry-e2e-boom%'"""));
                Assert.True(await CountAsync($"""SELECT count(*)::text FROM "ClientTelemetryEvents" WHERE {where} AND "Kind"='ApiStats' AND "Endpoint"='/api/mobile/plants/current' AND "Under10s" >= 1""") >= 1,
                    "慢 API 沒有記到 3～10 秒那一桶");
                Assert.True(await CountAsync($"""SELECT count(*)::text FROM "ClientTelemetryEvents" WHERE {where} AND "Kind"='Offline' AND "DurationMs" >= 500""") >= 1,
                    "斷線事件沒有回報");
                Assert.Equal(0, await CountAsync($"""SELECT count(*)::text FROM "ClientTelemetryEvents" WHERE {where} AND ("UserName" IS NULL OR "PlantId" IS NULL)"""));

                // 儀表板：桌機登入同一個帳號看得到這支手機
                var desk = await _fixture.NewPageAsync();
                await desk.GotoAsync($"{_fixture.BaseUrl}/FieldTelemetry?hours=24");
                await Assertions.Expect(desk.Locator("#deviceTable")).ToContainTextAsync(device[..8]);
                await Assertions.Expect(desk.Locator("#errorTable")).ToContainTextAsync("telemetry-e2e-boom");
                // desk 屬於共用 Context，由 fixture 下一次 NewPageAsync 回收，不可在這裡關 Context
            }
            finally
            {
                await ctx.CloseAsync();
                await SeedFixture.ExecAsync("""DELETE FROM "ClientTelemetryEvents" WHERE "Message" LIKE '%telemetry-e2e-boom%'""");
            }
        }
    }
}
