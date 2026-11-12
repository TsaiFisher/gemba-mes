using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 排程紀錄（2026-10-14）：每次全量排程寫一筆 ScheduleRunLog（含派工異動明細），排程頁「排程紀錄」頁籤讀取。
    /// 真 PG 才驗得到的部分：保留期清理走 ExecuteDelete＋資料庫層 ON DELETE CASCADE（InMemory 走另一條路徑）；
    /// 紀錄不是廠別實體、沒有全域過濾，端點必須自己擋別廠。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScheduleRunLogIntegrationTests
    {
        private const string Plant = "P1";
        private const string OtherPlant = "PX-RUNLOG";
        private readonly AuthedScheduleFactory _factory;

        public ScheduleRunLogIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task Run_WritesLog_PrunesExpired_AndEndpointsStayInsideAllowedPlants()
        {
            long expiredId, otherId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var expired = new ScheduleRunLog
                {
                    PlantId = Plant, StartedAt = DateTime.UtcNow.AddDays(-ScheduleRunLog.RetentionDays - 2),
                    FinishedAt = DateTime.UtcNow.AddDays(-ScheduleRunLog.RetentionDays - 2),
                    Trigger = "Manual", Rule = "Priority",
                    Changes = { new ScheduleRunChange { DispatchId = "RUNLOG-OLD", WorkOrderId = "RUNLOG-OLD", Kind = ScheduleChangeKind.Moved } }
                };
                var other = new ScheduleRunLog
                {
                    PlantId = OtherPlant, StartedAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow,
                    Trigger = "Manual", Rule = "Priority",
                    Changes = { new ScheduleRunChange { DispatchId = "RUNLOG-PX", WorkOrderId = "RUNLOG-PX", Kind = ScheduleChangeKind.Moved } }
                };
                db.ScheduleRunLogs.AddRange(expired, other);
                await db.SaveChangesAsync();
                expiredId = expired.RunLogId;
                otherId = other.RunLogId;
            }

            var client = _factory.CreateClient();
            var token  = await HarvestAntiForgeryTokenAsync(client);
            var before = DateTime.UtcNow.AddSeconds(-1);
            await RunAsync(client, token);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.False(await db.ScheduleRunLogs.AnyAsync(l => l.RunLogId == expiredId), "超過保留期的紀錄沒有被清掉");
                Assert.False(await db.ScheduleRunChanges.AnyAsync(c => c.DispatchId == "RUNLOG-OLD"), "明細沒有跟著紀錄刪除（缺 ON DELETE CASCADE？）");
                Assert.True(await db.ScheduleRunLogs.AnyAsync(l => l.RunLogId == otherId), "清理範圍越過了本廠");
                Assert.True(await db.ScheduleRunLogs.AnyAsync(l => l.PlantId == Plant && l.StartedAt >= before && l.Trigger == "Manual"),
                    "本次排程沒有寫紀錄");
            }

            // 排程紀錄：最新一筆就是剛剛那次
            var history = await client.GetStringAsync($"/Schedule/RunHistory?plantId={Plant}");
            long newestId;
            using (var doc = JsonDocument.Parse(history))
            {
                var rows = doc.RootElement.GetProperty("rows");
                Assert.True(rows.GetArrayLength() >= 1, history);
                Assert.Equal("Manual", rows[0].GetProperty("trigger").GetString());
                Assert.Equal("Priority", rows[0].GetProperty("rule").GetString());
                newestId = rows[0].GetProperty("id").GetInt64();
            }

            var changes = await client.GetAsync($"/Schedule/RunChanges/{newestId}");
            Assert.Equal(HttpStatusCode.OK, changes.StatusCode);

            // 別廠：清單擋 403、明細當作不存在
            var otherHistory = await client.GetAsync($"/Schedule/RunHistory?plantId={OtherPlant}");
            Assert.Equal(HttpStatusCode.Forbidden, otherHistory.StatusCode);
            var otherChanges = await client.GetAsync($"/Schedule/RunChanges/{otherId}");
            Assert.Equal(HttpStatusCode.NotFound, otherChanges.StatusCode);

            // 畫面有排程紀錄頁籤
            var html = await client.GetStringAsync("/Schedule");
            Assert.Contains("id=\"tabHistory\"", html);
        }

        private static async Task RunAsync(HttpClient client, string token)
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
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean(), body);
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
