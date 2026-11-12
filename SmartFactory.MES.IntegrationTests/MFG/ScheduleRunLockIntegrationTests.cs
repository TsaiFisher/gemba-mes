using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 排程同廠互斥（2026-10-06）：兩次排程同時跑會各自讀到同一份待排清單、先後寫回互相覆蓋。
    /// 引擎以 PostgreSQL 交易層級 advisory lock 鎖「mfg-schedule:廠別」，拿不到就回 Busy、什麼都不改。
    /// InMemory 單元測試沒有這個機制，只能在真 PG 驗。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScheduleRunLockIntegrationTests
    {
        private const string Plant = "P1";
        private readonly AuthedScheduleFactory _factory;

        public ScheduleRunLockIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task ScheduleRun_WhileAnotherRunHoldsPlantLock_ReturnsBusy_ThenSucceedsAfterRelease()
        {
            var client = _factory.CreateClient();
            var token  = await HarvestAntiForgeryTokenAsync(client);

            using (var scope = _factory.Services.CreateScope())
            {
                // 模擬「另一次排程正在跑」：另開連線、在交易內拿住同一把鎖
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtext('mfg-schedule:' || {0}))", Plant);

                var (ok, _) = await RunAsync(client, token);
                Assert.False(ok);

                await tx.RollbackAsync();   // 釋放鎖
            }

            var before = DateTime.UtcNow.AddSeconds(-1);
            var (ok2, body) = await RunAsync(client, token);
            Assert.True(ok2, body);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var state = await db.ScheduleRunStates.AsNoTracking().SingleAsync(s => s.PlantId == Plant);
                Assert.True(state.LastRunAt >= before, $"上次排程時間未更新：{state.LastRunAt:o}");
            }
        }

        /// <summary>
        /// 自動重排第 1 期：過時判定、日曆指紋（DateOnly 區間）、執行前預覽在真 PG 上要能翻成 SQL。
        /// 排完之後必須是「最新」——否則「異動後自動重排」會無限重排。
        /// </summary>
        [Fact]
        public async Task AfterRun_ScheduleIsFresh_AndPreviewAndBannerRender()
        {
            var client = _factory.CreateClient();
            var token  = await HarvestAntiForgeryTokenAsync(client);
            var (ok, body) = await RunAsync(client, token);
            Assert.True(ok, body);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var fresh = await SmartFactory.MES.Modules.MFG.Services.ScheduleFreshness.GetAsync(db, Plant);
                Assert.False(fresh.IsStale,
                    $"剛排完卻判定過時：first={fresh.FirstChangeAt:o} calendar={fresh.CalendarChanged}");
                Assert.Equal("Manual", fresh.LastTrigger);
            }

            var preview = await client.GetStringAsync($"/Schedule/RunPreview?plantId={Plant}");
            using (var doc = JsonDocument.Parse(preview))
            {
                Assert.True(doc.RootElement.GetProperty("scheduled").GetInt32() >= 0);
                Assert.True(doc.RootElement.GetProperty("tomorrow").GetInt32() >= 0);
            }

            var html = await client.GetStringAsync("/Schedule");
            Assert.Contains("id=\"scheduleStaleness\"", html);
        }

        private static async Task<(bool Success, string Body)> RunAsync(HttpClient client, string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/Schedule/Run?plantId={Plant}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["rule"] = "Priority" }),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"/Schedule/Run 回應非 2xx（{(int)resp.StatusCode}）：{body[..Math.Min(body.Length, 400)]}");
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.GetProperty("success").GetBoolean(), body);
        }

        private static async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能從 /Schedule 取得 AntiForgery token");
            return m.Groups[1].Value;
        }
    }
}
