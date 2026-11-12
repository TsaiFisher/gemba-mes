using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using BAS = SmartFactory.MES.Modules.BAS.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 混沌邊界：同一個 clientId 的兩個請求<b>同時</b>在伺服器上處理。
    ///
    /// <para><b>什麼時候會發生</b>：去重是「先查 SyncLog 有沒有這筆 → 處理 → 連同 SyncLog 一起存」。
    /// 兩個請求都在對方存檔前查完，就都會往下做，後存的那個撞 SyncLog 主鍵。現場會走到這裡的路徑：
    /// 回應在路上斷掉、手機轉排隊後立刻重送，而第一個請求還在伺服器上跑（換班尖峰資料庫慢）；
    /// 或同一支手機開了兩個分頁，兩邊的佇列同時補送同一批。</para>
    ///
    /// <para><b>錯在哪</b>：撞主鍵原本被全站的唯一鍵過濾器轉成 409（手機請求沒有 X-Requested-With），
    /// 佇列把非並發衝突的 409 標成「失敗、需人工處理」，直接送出的畫面則顯示錯誤——但這筆其實
    /// 已經由另一個請求做完了。人看到失敗就會重報一次（新的 clientId），那才是真正的重複。</para>
    ///
    /// <para>用存檔攔截器在「通過去重檢查之後、存檔之前」以另一條連線先寫入同一個 clientId，
    /// 確定性地重現交錯；HTTP 平行打兩發要看運氣。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MobileDuplicateInFlightTests
    {
        private const string Plant = "P1";
        private const string Wc = "CHAOS-WC";
        private const string Machine = "CHAOS-M1";

        private readonly AuthedScheduleFactory _factory;

        public MobileDuplicateInFlightTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        /// <summary>存檔時若本次要寫的 SyncLog 帶指定 clientId，先用另一條連線把「另一個請求的結果」寫進去。</summary>
        private sealed class RaceInjector : SaveChangesInterceptor, IModuleSaveChangesInterceptor
        {
            private readonly string _clientId;
            private readonly string _connectionString;
            public bool Fired;

            public RaceInjector(string clientId, string connectionString)
            {
                _clientId = clientId;
                _connectionString = connectionString;
            }

            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                var ctx = eventData.Context;
                if (!Fired && ctx != null && ctx.ChangeTracker.Entries<SyncLog>()
                        .Any(e => e.State == EntityState.Added && e.Entity.ClientId == _clientId))
                {
                    Fired = true;
                    await using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(cancellationToken);
                    await using var cmd = new NpgsqlCommand(
                        """
                        INSERT INTO "SyncLogs" ("ClientId","Endpoint","ClientTimestamp","CreatedAt","ResponseSnapshot")
                        VALUES (@id, 'race', now(), now(), @snap)
                        """, conn);
                    cmd.Parameters.AddWithValue("id", _clientId);
                    cmd.Parameters.AddWithValue("snap",
                        """{"Success":true,"Data":null,"Message":"first-request-result"}""");
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                return result;
            }
        }

        [Fact]
        public async Task 同一筆同時處理兩次_後到的回第一個請求的結果_不重複寫入也不報失敗()
        {
            var clientId = $"CHAOS-RACE-{Guid.NewGuid():N}";
            string cs;
            using (var scope = _factory.Services.CreateScope())
                cs = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!;

            var injector = new RaceInjector(clientId, cs);
            using var app = _factory.WithWebHostBuilder(b =>
                b.ConfigureServices(s =>
                {
                    s.AddScoped<IModuleSaveChangesInterceptor>(_ => injector);
                    s.AddScoped<IMobilePermissionService, AllowAllMobilePermission>();   // 受測的是去重，不是手機權限
                }));
            var client = app.CreateClient();

            var ordersBefore = await CountOrdersAsync();
            var resp = await client.PostAsJsonAsync($"/api/mobile/machines/{Machine}/status-event",
                new { eventType = "II", note = "chaos", sourceDocId = (string?)null, clientId, clientTimestamp = DateTime.UtcNow });
            var body = await resp.Content.ReadAsStringAsync();

            Assert.True(injector.Fired, $"攔截器沒有觸發——請求沒有走到寫 SyncLog 的存檔，本測試什麼都沒驗到。HTTP {(int)resp.StatusCode}：{body}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean(), body);
            Assert.Equal("first-request-result", json.RootElement.GetProperty("message").GetString());

            // 這一個請求的業務變更與它的 SyncLog 在同一次存檔，撞主鍵就一起不存在
            Assert.Equal(ordersBefore, await CountOrdersAsync());

            // 之後的重送（佇列再補一次）照常走去重，拿回同一份結果
            var again = await client.PostAsJsonAsync($"/api/mobile/machines/{Machine}/status-event",
                new { eventType = "II", note = "chaos", sourceDocId = (string?)null, clientId, clientTimestamp = DateTime.UtcNow });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(ordersBefore, await CountOrdersAsync());
        }

        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<SmartFactory.MES.Models.MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<SmartFactory.MES.Models.MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey) => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult<IEnumerable<string>>(new[] { "view" });
        }

        private async Task<int> CountOrdersAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.MachineStatusOrderLines.AsNoTracking().CountAsync(l => l.MachineCode == Machine);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == Wc))
            {
                db.WorkCenters.Add(new BAS.WorkCenter { WorkCenterCode = Wc, WorkCenterName = "CHAOS WC", IsActive = true, PlantId = Plant });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine { WorkCenterCode = Wc, MachineCode = Machine, MachineName = "CHAOS 機台" });
                db.SaveChanges();
            }
        }
    }
}
