using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPT
{
    /// <summary>
    /// 現場遙測走真 PostgreSQL：上報端點、寫入（參數化 SQL，不經 SaveChanges）、限流、儀表板彙總。
    ///
    /// <para>寫入是手寫的多列 INSERT，型別逐欄指定——InMemory 驗不到「整欄都是 NULL 時參數被推成 text」
    /// 那種錯，所以放在整合測試。每支用自己的 DeviceId，彙總斷言只看自己那幾筆。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ClientTelemetryIntegrationTests
    {
        private readonly AuthedScheduleFactory _factory;
        public ClientTelemetryIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        private static object Batch(string deviceId, params object[] events) =>
            new { deviceId, appVersion = "mes-mobile-v36", events };

        [Fact]
        public async Task 登入後上報_寫入時帶伺服器端的使用者與廠別_路徑正規化_不產生稽核紀錄()
        {
            var device = $"it-{Guid.NewGuid():N}"[..30];
            var client = _factory.CreateClient();
            var auditBefore = await CountAsync(db => db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityType == nameof(ClientTelemetryEvent)));

            var resp = await client.PostAsJsonAsync("/api/telemetry/events", Batch(device,
                new { kind = "Error", message = "x is not defined", page = "/mobile/mfg/reports.html?d=1", detail = "at foo" },
                new { kind = "ApiStats", endpoint = "/api/mobile/sessions/3f2a9c1e-1111-4000-8000-000000000001/complete",
                      count = 4, durationMs = 13000, maxMs = 9000, under1s = 1, under3s = 1, under10s = 2, over10s = 0, failures = 1 },
                // 除了種類以外全部是 null：多列 VALUES 裡整欄 NULL 的參數不指定型別就會被推成 text
                new { kind = "Offline" },
                new { kind = "Queue", queuePending = 3, queueOldestMinutes = 42 }));
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

            var rows = await RowsAsync(device);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => { Assert.Equal("P1", r.PlantId); Assert.Equal("admin", r.UserName); Assert.Equal("mes-mobile-v36", r.AppVersion); });
            Assert.Equal("/mobile/mfg/reports.html", rows.Single(r => r.Kind == "Error").Page);
            Assert.Equal("/api/mobile/sessions/{id}/complete", rows.Single(r => r.Kind == "ApiStats").Endpoint);
            Assert.Equal(3, rows.Single(r => r.Kind == "Queue").QueuePending);

            // 寫入不經 SaveChanges：稽核攔截器不會替每一筆遙測記一筆 AuditLog
            Assert.Equal(auditBefore, await CountAsync(db => db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityType == nameof(ClientTelemetryEvent))));
        }

        [Fact]
        public async Task 未登入只收錯誤_而且沒有使用者與廠別()
        {
            var device = $"it-{Guid.NewGuid():N}"[..30];
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IClientTelemetryService>();
            var written = await svc.IngestAsync(new ClientTelemetryBatch(device, null, new()
            {
                new("Error", null, "/mobile/index.html", "login boom", null, null, null, null, null, null, null, null, null, null, null, null, null, null),
                new("Queue", null, "/mobile/index.html", null, null, null, null, null, null, null, null, null, null, null, 9, 1, null, null),
            }), new ClientTelemetrySource(null, null, null, $"198.51.100.{Random.Shared.Next(1, 250)}", "UA"));

            Assert.Equal(1, written);
            var row = Assert.Single(await RowsAsync(device));
            Assert.Equal(("Error", (string?)null, (string?)null), (row.Kind, row.UserId, row.PlantId));
        }

        [Fact]
        public async Task 同一IP每分鐘超過上限就拒收()
        {
            var device = $"it-{Guid.NewGuid():N}"[..30];
            var ip = $"203.0.113.{Random.Shared.Next(1, 250)}";
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IClientTelemetryService>();
            var source = new ClientTelemetrySource("u", "u", "P1", ip, null);
            var batch = new ClientTelemetryBatch(device, null, new()
            {
                new("Error", null, null, "spam", null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            });

            var results = new List<int>();
            for (var i = 0; i < ClientTelemetryService.MaxBatchesPerMinutePerIp + 3; i++)
                results.Add(await svc.IngestAsync(batch, source));

            // 跨分鐘邊界時計數器會重來，所以只斷言「有被拒」與「沒有全部收下」
            Assert.Contains(-1, results);
            Assert.True((await RowsAsync(device)).Count <= ClientTelemetryService.MaxBatchesPerMinutePerIp * 2);
        }

        [Fact]
        public async Task 儀表板_裝置現況取最後一次佇列_錯誤與API彙總_頁面打得開()
        {
            var device = $"it-{Guid.NewGuid():N}"[..30];
            // 端點要純字母：帶數字的片段會被正規化成 {id}（那正是設計），就分不出是這支測試的了
            var probe = "/api/mobile/probe-" + new string(Guid.NewGuid().ToByteArray().Select(b => (char)('a' + b % 26)).ToArray());
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/api/telemetry/events", Batch(device,
                new { kind = "Queue", queuePending = 5, queueOldestMinutes = 10 }));
            await client.PostAsJsonAsync("/api/telemetry/events", Batch(device,
                new { kind = "Queue", queuePending = 2, queueOldestMinutes = 12 },
                new { kind = "Error", message = $"boom-{device}", page = "/mobile/home.html", count = 3 },
                new { kind = "ApiStats", endpoint = probe, count = 10, durationMs = 50000, maxMs = 15000,
                      under1s = 5, under3s = 2, under10s = 2, over10s = 1, failures = 1, conflicts = 2 }));

            using (var scope = _factory.Services.CreateScope())
            {
                var report = scope.ServiceProvider.GetRequiredService<IFieldTelemetryReportService>();
                var vm = await report.BuildAsync("P1", 24, null, DateTime.UtcNow);

                var dev = Assert.Single(vm.Devices, d => d.DeviceId == device);
                Assert.Equal(2, dev.QueuePending);                  // 最後一次，不是加總
                Assert.Equal(12, dev.QueueOldestMinutes);
                Assert.True(vm.DevicesWithPending >= 1);
                Assert.Equal(3, Assert.Single(vm.TopErrors, e => e.Message == $"boom-{device}").Count);
                var ep = Assert.Single(vm.Endpoints, e => e.Endpoint == probe);
                Assert.Equal((10, 5000L, 1, 1, 2), (ep.Calls, ep.AvgMs, ep.Over10s, ep.Failures, ep.Conflicts));
                Assert.True(vm.ApiConflicts >= 2);
            }

            var page = await client.GetAsync("/FieldTelemetry?hours=24");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(device[..8], await page.Content.ReadAsStringAsync());
        }

        private async Task<List<ClientTelemetryEvent>> RowsAsync(string device)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ClientTelemetryEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.DeviceId == device).OrderBy(e => e.Id).ToListAsync();
        }

        private async Task<int> CountAsync(Func<ApplicationDbContext, Task<int>> q)
        {
            using var scope = _factory.Services.CreateScope();
            return await q(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }
    }
}
