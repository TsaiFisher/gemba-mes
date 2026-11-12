using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 排程紀錄頁籤與自動重排規則／凍結時界欄位（2026-10-14）的守門。
    ///
    /// <para><b>刻意唯讀：</b>不按「執行排程」、不存系統參數——那會重配 E2E 庫的派工、改全域參數，
    /// 讓同一輪的流程測試（備料、報工）吃到不同的派工時段。排程紀錄本身有沒有寫對，
    /// 由單元測試 ScheduleRunLogTests 與整合測試 ScheduleRunLogIntegrationTests 驗；
    /// 這裡只驗「真畫面打得開、資料畫得出來、沒有 JS 錯誤」。</para>
    ///
    /// <para><b>沒有紀錄時不判失敗：</b>乾淨部署從未排過程，紀錄為空是正常狀態（同 ScheduleGanttTests 的理由）。
    /// 有紀錄時才往下點開明細，並把走了哪一條印出來。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ScheduleRunHistoryTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public ScheduleRunHistoryTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        // 與 ScheduleGanttTests 相同的兩則已知雜訊（甘特是預設頁籤，開頁就會畫）：
        // timelines-chart 注入的 <style> 被 CSP 擋、TimeOverview 的上游 null domain 競態。
        private static bool IsKnownGanttNoise(string text) =>
            (text.Contains("style-src", StringComparison.Ordinal) && text.Contains("Applying inline style", StringComparison.Ordinal))
            || text.Contains("Cannot read properties of null (reading 'domain')", StringComparison.Ordinal);

        [Fact]
        public async Task RunHistoryTab_RendersRowsOrEmptyState_AndOpensChanges()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add("pageerror: " + e);
            page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };

            await page.GotoAsync(_fx.BaseUrl + "/Schedule");
            await page.Locator("#scheduleTabs button[data-tab='history']").ClickAsync();

            // 三個終局之一：表格、空狀態、載入失敗（失敗也要等得到，否則只會看到無資訊的逾時）
            await page.WaitForFunctionAsync(
                "() => { const t = document.getElementById('historyTable'); return !!t && (t.querySelector('table') || t.querySelector('p')); }",
                null, new PageWaitForFunctionOptions { Timeout = 20_000 });

            Assert.Equal(0, await page.Locator("#historyTable p.text-danger").CountAsync());
            Assert.False(await page.Locator("#legendBar").IsVisibleAsync(), "切到排程紀錄時甘特圖例應收起");

            var rows = page.Locator("#historyTable tr[data-run-id]");
            var count = await rows.CountAsync();
            if (count == 0)
            {
                _out.WriteLine("走到「尚無排程紀錄」分支（從未排過程，乾淨部署的正常狀態）");
            }
            else
            {
                _out.WriteLine($"排程紀錄 {count} 列，點開第一列");
                Assert.Equal(10, await page.Locator("#historyTable thead th").CountAsync());
                await rows.First.ClickAsync();
                await page.WaitForFunctionAsync(
                    "() => { const c = document.getElementById('historyChanges'); return !!c && (c.querySelector('table') || c.querySelector('p')); }",
                    null, new PageWaitForFunctionOptions { Timeout = 20_000 });
                Assert.Equal(0, await page.Locator("#historyChanges p.text-danger").CountAsync());
                Assert.Contains("table-active", await rows.First.GetAttributeAsync("class") ?? "");
            }

            var real = errors.Where(e => !IsKnownGanttNoise(e)).ToList();
            Assert.True(real.Count == 0, $"排程紀錄頁籤有 JS／主控台錯誤：\n  {string.Join("\n  ", real)}");
        }

        [Fact]
        public async Task RunHistoryEndpoint_ReturnsJsonForOwnPlant()
        {
            var page = await _fx.NewLoggedInPageAsync();
            await page.GotoAsync(_fx.BaseUrl + "/Schedule");

            // 用頁面內 fetch：APIRequest 在 http 下不送 Secure cookie（見 UITests 本機執行注意事項）
            var body = await page.EvaluateAsync<string>(
                "async () => { const r = await fetch('/Schedule/RunHistory', { headers: { 'X-Requested-With': 'XMLHttpRequest' } }); return r.status + '\\n' + await r.text(); }");
            var status = body[..body.IndexOf('\n')];
            Assert.True(status == "200", $"RunHistory 回 {status}：{body[..Math.Min(body.Length, 300)]}");
            using var doc = JsonDocument.Parse(body[(body.IndexOf('\n') + 1)..]);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("rows").ValueKind);
        }

        [Fact]
        public async Task SystemParam_ShowsAutoScheduleRuleAndFreezeWindow()
        {
            var page = await _fx.NewLoggedInPageAsync();
            await page.GotoAsync(_fx.BaseUrl + "/SystemParam");

            var rule = page.Locator("#mfgAutoScheduleRule");
            Assert.Equal(1, await rule.CountAsync());
            // 「未設定」＋五種規則；值必須是列舉名稱，存檔端只認這些
            var values = await rule.Locator("option").EvaluateAllAsync<string[]>("os => os.map(o => o.value)");
            Assert.Equal(new[] { "", "Priority", "EDD", "SPT", "FIFO", "LIFO" }, values);

            var freeze = page.Locator("#mfgAutoScheduleFreezeHours");
            Assert.Equal("number", await freeze.GetAttributeAsync("type"));
            Assert.Equal("168", await freeze.GetAttributeAsync("max"));
        }
    }
}
