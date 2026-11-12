using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using BAS = SmartFactory.MES.Modules.BAS.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Load
{
    /// <summary>
    /// 換班尖峰：上一班的人同時報最後一批、下工，下一班的人同時上工、開始報工——全部擠在同幾條生產線的同幾張派工上。
    ///
    /// <para><b>要回答的問題</b>：①有沒有 5xx 或死結；②並發衝突（xmin）時照手機佇列的規則重送（同一個 clientId、最多 5 次），
    /// 最後是不是每一個動作都恰好生效一次——派工完成數等於所有被接受的報工數量加總，一筆不多一筆不少；
    /// ③每支 API 在尖峰下的 p50／p95／最長耗時，與資料庫同時有幾個連線在等鎖。</para>
    ///
    /// <para>走完整 HTTP 管線（WebApplicationFactory，含例外過濾器），打真 PostgreSQL。每個虛擬作業員是一個真的使用者帳號
    /// （手機端點規定作業員必須是登入者本人），以請求標頭 <c>X-Load-User</c> 切換身分。另有 <c>LOAD_DUP_RATE</c> 比例的動作
    /// 同時送兩次同一個 clientId（回應遺失後重送與第一次撞在一起，見 MobileDuplicateInFlightTests）。</para>
    ///
    /// <para>執行：<c>LOAD_TEST=1 ConnectionStrings__DefaultConnection=…（專屬庫） dotnet test --filter ShiftChangeLoadTests</c>；
    /// 規模可調：LOAD_LINES（生產線＝派工數，預設 10）、LOAD_OPS_PER_LINE（每班每線人數，預設 3）、
    /// LOAD_REPORTS（新班每人報工次數，預設 3）、LOAD_DUP_RATE（預設 0.1）。報告寫到測試輸出與 load-report-*.md。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ShiftChangeLoadTests
    {
        /// <summary>
        /// 專屬廠別：不可用 P1——種下的是數量十萬、生產中的派工，P1 的排程整合測試會想把它們排進去，
        /// 90 天內排不完而整批失敗（2026-10-21 實際踩到）。跑完一律清掉這一輪的資料（<see cref="Cleanup"/>）。
        /// </summary>
        private const string Plant = "ZZLOAD";
        private const string RoleId = "1";
        private const string LoadUserHeader = "X-Load-User";
        private const int MaxConflictRetries = 5;

        private readonly AuthedScheduleFactory _factory;
        private readonly ITestOutputHelper _out;

        public ShiftChangeLoadTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _out = output;
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        private static double EnvD(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // ════════════════════════════════════════════════════════════════════

        [LoadTestFact]
        public async Task 換班尖峰_無5xx無死結_每個動作恰好生效一次_報告耗時與鎖等待()
        {
            var lines = Env("LOAD_LINES", 10);
            var opsPerLine = Env("LOAD_OPS_PER_LINE", 3);
            var reportsPerNewOp = Env("LOAD_REPORTS", 3);
            var dupRate = EnvD("LOAD_DUP_RATE", 0.1);
            var run = Guid.NewGuid().ToString("N")[..6];
            var seed = Seed(run, lines, opsPerLine);
            try
            {
                await RunAsync(run, seed, lines, opsPerLine, reportsPerNewOp, dupRate);
            }
            finally
            {
                Cleanup(run);
            }
        }

        private async Task RunAsync(string run, SeedResult seed, int lines, int opsPerLine, int reportsPerNewOp, double dupRate)
        {

            using var app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, LoadAuthHandler>(LoadAuthHandler.SchemeName, _ => { });
                s.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = LoadAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = LoadAuthHandler.SchemeName;
                    o.DefaultScheme = LoadAuthHandler.SchemeName;
                });
                s.AddScoped<IMobilePermissionService, AllowAllMobilePermission>();   // 受測的是並發，不是手機權限
            }));
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Remove("X-Plant-Context");
            client.DefaultRequestHeaders.Add("X-Plant-Context", Plant);
            client.Timeout = TimeSpan.FromMinutes(5);

            var stats = new ConcurrentBag<Sample>();
            var accepted = new ConcurrentDictionary<string, (string DispatchId, decimal Qty, bool IsReport)>();
            var failures = new ConcurrentBag<string>();
            var rng = new Random(42);
            var dupFlags = Enumerable.Range(0, 100_000).Select(_ => rng.NextDouble() < dupRate).ToArray();
            var dupIndex = -1;
            bool NextDup() => dupFlags[Interlocked.Increment(ref dupIndex) % dupFlags.Length];

            // 一個動作：照手機佇列的規則送——並發衝突（409 CONCURRENCY_CONFLICT）用同一個 clientId 重送
            async Task SendAsync(string user, string op, HttpMethod method, string url, object body, string clientId,
                string dispatchId, decimal qty, bool isReport)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var sends = NextDup() ? 2 : 1;   // 偶爾同時送兩次（回應遺失後立刻重送）
                    var tasks = Enumerable.Range(0, sends).Select(async _ =>
                    {
                        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
                        req.Headers.Add(LoadUserHeader, user);
                        var sw = Stopwatch.StartNew();
                        using var res = await client.SendAsync(req);
                        var text = await res.Content.ReadAsStringAsync();
                        stats.Add(new Sample(op, (int)res.StatusCode, sw.Elapsed.TotalMilliseconds, attempt));
                        return (res.StatusCode, text);
                    }).ToList();
                    var results = await Task.WhenAll(tasks);

                    if (results.Any(r => (int)r.StatusCode is >= 200 and < 300))
                    {
                        accepted.TryAdd(clientId, (dispatchId, qty, isReport));
                        return;
                    }
                    var conflict = results.All(r => r.StatusCode == HttpStatusCode.Conflict && r.text.Contains("CONCURRENCY_CONFLICT"));
                    if (conflict && attempt < MaxConflictRetries)
                    {
                        await Task.Delay(Random.Shared.Next(20, 120) * (attempt + 1));
                        continue;
                    }
                    failures.Add($"{op} {user} {url}: {string.Join(" | ", results.Select(r => $"{(int)r.StatusCode} {Trim(r.text)}"))}");
                    return;
                }
            }

            static string Trim(string s) => s.Length > 200 ? s[..200] : s;

            // 上一班：報最後一批、下工（下工帶最後一批數量）
            async Task OldShiftAsync(OldOp o)
            {
                var c1 = Cid();
                await SendAsync(o.User, "報工(上一班)", HttpMethod.Post, "/api/mobile/reports",
                    new { sessionId = o.SessionId, qtyOk = 5, qtyNg = 0, clientId = c1, clientTimestamp = DateTime.UtcNow },
                    c1, o.DispatchId, 5, true);
                var c2 = Cid();
                await SendAsync(o.User, "完工下工", HttpMethod.Patch, $"/api/mobile/sessions/{o.SessionId}/complete",
                    new { clientId = c2, clientTimestamp = DateTime.UtcNow, qtyOk = 3, qtyNg = 0 },
                    c2, o.DispatchId, 3, true);
            }

            // 下一班：上工，接著報幾次工
            async Task NewShiftAsync(NewOp n)
            {
                var c0 = Cid();
                await SendAsync(n.User, "上工", HttpMethod.Post, "/api/mobile/sessions",
                    new { dispatchId = n.DispatchId, machineId = (string?)null, moldId = (string?)null, operatorId = n.UserId,
                          clientId = c0, clientTimestamp = DateTime.UtcNow },
                    c0, n.DispatchId, 0, false);
                var sessionId = await SessionOfAsync(n.UserId, n.DispatchId);
                if (sessionId == null) { failures.Add($"上工後查不到 {n.User} 的工作階段"); return; }
                for (var i = 0; i < reportsPerNewOp; i++)
                {
                    var c = Cid();
                    await SendAsync(n.User, "報工(下一班)", HttpMethod.Post, "/api/mobile/reports",
                        new { sessionId, qtyOk = 2, qtyNg = 0, clientId = c, clientTimestamp = DateTime.UtcNow },
                        c, n.DispatchId, 2, true);
                }
            }

            // 暖機：剛建好的 app 第一批請求含 JIT、連線池建立，不暖機的話第一波耗時量到的是冷啟動
            foreach (var u in seed.Old.Select(o => o.User).Concat(seed.New.Select(n => n.User)).Take(5))
            {
                using var warm = new HttpRequestMessage(HttpMethod.Get, "/api/mobile/plants/current");
                warm.Headers.Add(LoadUserHeader, u);
                (await client.SendAsync(warm)).Dispose();
            }

            var deadlocksBefore = await DeadlocksAsync();
            using var lockCts = new CancellationTokenSource();
            var lockSampler = SampleLockWaitsAsync(lockCts.Token);

            // 所有人在同一刻起跑
            var gate = new TaskCompletionSource();
            var all = seed.Old.Select(o => Task.Run(async () => { await gate.Task; await OldShiftAsync(o); }))
                .Concat(seed.New.Select(n => Task.Run(async () => { await gate.Task; await NewShiftAsync(n); })))
                .ToList();
            var wall = Stopwatch.StartNew();
            gate.SetResult();
            await Task.WhenAll(all);
            wall.Stop();
            lockCts.Cancel();
            var (lockMax, lockSamplesWaiting, lockSamples) = await lockSampler;
            var deadlocks = await DeadlocksAsync() - deadlocksBefore;

            // ── 驗證：每個被接受的動作恰好生效一次 ─────────────────────────────
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dispatchIds = seed.Dispatches.Keys.ToList();
            var dispatches = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => dispatchIds.Contains(d.DispatchId)).ToDictionaryAsync(d => d.DispatchId);
            var mismatches = new List<string>();
            foreach (var (id, initial) in seed.Dispatches)
            {
                var expected = initial + accepted.Values.Where(a => a.IsReport && a.DispatchId == id).Sum(a => a.Qty);
                if (dispatches[id].QtyDone != expected)
                    mismatches.Add($"{id}: QtyDone {dispatches[id].QtyDone}，應為 {expected}");
            }
            var reportRows = await db.ProductionReports.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(r => dispatchIds.Contains(r.DispatchId!));
            var acceptedReports = accepted.Values.Count(a => a.IsReport && a.Qty > 0);
            var oldOpen = await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(s => seed.Old.Select(o => o.SessionId).Contains(s.SessionId) && s.EndAt == null);
            var newOpen = await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(s => dispatchIds.Contains(s.DispatchId) && s.EndAt == null);

            var report = BuildReport(run, lines, opsPerLine, reportsPerNewOp, dupRate, wall.Elapsed, stats, failures,
                deadlocks, lockMax, lockSamplesWaiting, lockSamples, mismatches, reportRows, acceptedReports, oldOpen, newOpen);
            _out.WriteLine(report);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"load-report-{run}.md"), report);

            Assert.True(stats.All(s => s.Status < 500), "有 5xx：\n" + report);
            Assert.Equal(0, deadlocks);
            Assert.Empty(failures);
            Assert.Empty(mismatches);
            Assert.Equal(0, oldOpen);
            Assert.Equal(seed.New.Count, newOpen);
        }

        // ── 動作與 clientId ─────────────────────────────────────────────────

        private static string Cid() => $"LOAD-C-{Guid.NewGuid():N}";

        private async Task<string?> SessionOfAsync(string userId, string dispatchId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.DispatchId == dispatchId && s.OperatorId == userId && s.EndAt == null)
                .Select(s => s.SessionId).FirstOrDefaultAsync();
        }

        // ── 資料庫觀測 ────────────────────────────────────────────────────

        private string ConnectionString()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!;
        }

        private async Task<long> DeadlocksAsync()
        {
            await using var conn = new NpgsqlConnection(ConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        /// <summary>每 50ms 數一次「正在等鎖」的連線：最大同時等待數，與有人在等的取樣比例。</summary>
        private async Task<(int Max, int Waiting, int Total)> SampleLockWaitsAsync(CancellationToken ct)
        {
            int max = 0, waiting = 0, total = 0;
            await using var conn = new NpgsqlConnection(ConnectionString());
            await conn.OpenAsync(CancellationToken.None);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
                    var n = (int)(long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!;
                    total++;
                    if (n > 0) waiting++;
                    max = Math.Max(max, n);
                    await Task.Delay(50, ct);
                }
            }
            catch (OperationCanceledException) { }
            return (max, waiting, total);
        }

        // ── 報告 ──────────────────────────────────────────────────────────

        private sealed record Sample(string Op, int Status, double Ms, int Attempt);

        private static string BuildReport(string run, int lines, int opsPerLine, int reportsPerNewOp, double dupRate, TimeSpan wall,
            IEnumerable<Sample> stats, IEnumerable<string> failures, long deadlocks, int lockMax, int lockWaiting, int lockSamples,
            List<string> mismatches, int reportRows, int acceptedReports, int oldOpen, int newOpen)
        {
            static double P(List<double> xs, double p) => xs.Count == 0 ? 0 : xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(p * xs.Count) - 1)];
            var sb = new StringBuilder();
            sb.AppendLine($"# 換班尖峰壓測 {run}（{DateTime.Now:yyyy-MM-dd HH:mm}）");
            sb.AppendLine($"規模：{lines} 條線 × 每班 {opsPerLine} 人（兩班共 {lines * opsPerLine * 2} 人同時起跑），新班每人報工 {reportsPerNewOp} 次，同時重送比例 {dupRate:P0}");
            sb.AppendLine($"總耗時 {wall.TotalSeconds:N1} 秒；死結 {deadlocks}；等鎖連線最多同時 {lockMax} 個，{lockWaiting}/{lockSamples} 次取樣有人在等");
            sb.AppendLine();
            sb.AppendLine("| 動作 | 請求數 | 2xx | 409 | 其他4xx | 5xx | 重送輪次>0 | p50 ms | p95 ms | 最長 ms |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in stats.GroupBy(s => s.Op).OrderBy(g => g.Key))
            {
                var ms = g.Select(s => s.Ms).OrderBy(x => x).ToList();
                sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(s => s.Status is >= 200 and < 300)} | {g.Count(s => s.Status == 409)} | " +
                              $"{g.Count(s => s.Status is >= 400 and < 500 and not 409)} | {g.Count(s => s.Status >= 500)} | {g.Count(s => s.Attempt > 0)} | " +
                              $"{P(ms, 0.5):N0} | {P(ms, 0.95):N0} | {ms.LastOrDefault():N0} |");
            }
            sb.AppendLine();
            sb.AppendLine($"報工列數 {reportRows}（被接受的報工動作 {acceptedReports}）；上一班未下工 {oldOpen}；下一班在線 {newOpen}");
            if (mismatches.Count > 0) sb.AppendLine("**完成數不符**：\n- " + string.Join("\n- ", mismatches));
            var f = failures.ToList();
            if (f.Count > 0) sb.AppendLine($"**重送後仍失敗 {f.Count} 個動作**：\n- " + string.Join("\n- ", f.Take(30)));
            return sb.ToString();
        }

        // ── 種資料 ────────────────────────────────────────────────────────

        private sealed record OldOp(string User, string UserId, string DispatchId, string SessionId);
        private sealed record NewOp(string User, string UserId, string DispatchId);
        private sealed record SeedResult(Dictionary<string, decimal> Dispatches, List<OldOp> Old, List<NewOp> New);

        private SeedResult Seed(string run, int lines, int opsPerLine)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            if (!db.Plants.Any(p => p.Id == Plant))
            {
                db.Plants.Add(new Plant { Id = Plant, Name = "LOAD 壓測專用", DefaultTimeZone = "Asia/Taipei" });
                db.SaveChanges();
            }
            var wc = $"LOAD-WC-{run}";
            db.WorkCenters.Add(new BAS.WorkCenter { WorkCenterCode = wc, WorkCenterName = $"LOAD {run}", IsProductionLine = true, IsActive = true, PlantId = Plant });

            var users = new List<ApplicationUser>();
            ApplicationUser NewUser(string name)
            {
                var u = new ApplicationUser
                {
                    Id = Guid.NewGuid().ToString(), UserName = name, NormalizedUserName = name.ToUpperInvariant(),
                    Email = $"{name}@load.test", NormalizedEmail = $"{name}@LOAD.TEST".ToUpperInvariant(),
                    SecurityStamp = Guid.NewGuid().ToString(), ActivePlantId = Plant, ActiveRoleId = RoleId,
                    MustChangePassword = false, TwoFactorEnabled = true,
                };
                users.Add(u);
                return u;
            }

            var dispatches = new Dictionary<string, decimal>();
            var old = new List<OldOp>();
            var fresh = new List<NewOp>();
            for (var l = 0; l < lines; l++)
            {
                var m = $"LOAD-M{l:00}-{run}";
                var b = $"LOAD-B{l:00}-{run}";
                var wo = $"LOAD-WO{l:00}-{run}";
                var d = $"LOAD-D{l:00}-{run}";
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine { WorkCenterCode = wc, MachineCode = m, MachineName = m });
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = b, MrpRunId = "LOAD-RUN", MrpRunLineSeq = 1, ProductCode = "LOAD-P", Quantity = 100_000,
                    Priority = 50, PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = wo, ProductionBatchId = b, ProductCode = "LOAD-P", RoutingId = $"LOAD-R-{run}",
                    Quantity = 100_000, Status = MfgM.WorkOrderStatus.InProgress, PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = wo, OperationSeq = 10, WorkCenterId = wc, UnitTime = 1, BatchQty = 1,
                    OperationName = "LOAD OP", RequiresInspection = false, RequiresFpi = false,
                });
                db.Dispatches.Add(new MfgM.Dispatch
                {
                    DispatchId = d, WorkOrderId = wo, OperationSeq = 10, Quantity = 100_000, QtyDone = 100,
                    Status = MfgM.DispatchStatus.InProgress, MachineCode = m, ScheduledStart = now.AddHours(-8), ScheduledEnd = now.AddHours(8),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                dispatches[d] = 100;

                for (var o = 0; o < opsPerLine; o++)
                {
                    var ou = NewUser($"load-{run}-old-{l:00}-{o}");
                    var sid = $"LOAD-S{l:00}{o}-{run}";
                    db.ProductionSessions.Add(new MfgM.ProductionSession
                    {
                        SessionId = sid, DispatchId = d, WorkOrderId = wo, Status = MfgM.ProductionSessionStatus.Open,
                        StartAt = now.AddHours(-7), OperatorId = ou.Id, MachineCode = m, PlantId = Plant,
                    });
                    old.Add(new OldOp(ou.UserName!, ou.Id, d, sid));
                    var nu = NewUser($"load-{run}-new-{l:00}-{o}");
                    fresh.Add(new NewOp(nu.UserName!, nu.Id, d));
                }
            }
            db.Users.AddRange(users);
            db.SaveChanges();
            db.UserRoles.AddRange(users.Select(u => new IdentityUserRole<string> { UserId = u.Id, RoleId = RoleId }));
            db.SaveChanges();
            return new SeedResult(dispatches, old, fresh);
        }

        /// <summary>刪掉這一輪種的資料（依代號尾碼）。成本與傳票等衍生列留在專屬廠別裡，不影響其他測試。</summary>
        private void Cleanup(string run)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var like = $"%-{run}";
            db.Database.ExecuteSqlRaw("""
                DELETE FROM "ProductionReports" WHERE "DispatchId" LIKE @p0;
                DELETE FROM "ProductionSessions" WHERE "DispatchId" LIKE @p0;
                DELETE FROM "Dispatches" WHERE "DispatchId" LIKE @p0;
                DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId" LIKE @p0;
                DELETE FROM "WorkOrders" WHERE "WorkOrderId" LIKE @p0;
                DELETE FROM "ProductionBatches" WHERE "BatchId" LIKE @p0;
                DELETE FROM "WorkCenterMachines" WHERE "WorkCenterCode" LIKE @p0;
                DELETE FROM "WorkCenters" WHERE "WorkCenterCode" LIKE @p0;
                DELETE FROM "AspNetUserRoles" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "UserName" LIKE @p1);
                DELETE FROM "AspNetUsers" WHERE "UserName" LIKE @p1;
                """, new Npgsql.NpgsqlParameter("p0", like), new Npgsql.NpgsqlParameter("p1", $"load-{run}-%"));
        }

        // ── 測試替身 ──────────────────────────────────────────────────────

        /// <summary>依 <c>X-Load-User</c> 標頭登入成那個使用者（角色 claim 取自實際授予，見 TestAuthHandler）。</summary>
        private sealed class LoadAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string SchemeName = "LoadScheme";

            public LoadAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder) { }

            protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var name = Request.Headers[LoadUserHeader].ToString();
                if (string.IsNullOrEmpty(name)) name = "admin";
                var users = Context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByNameAsync(name);
                if (user == null) return AuthenticateResult.NoResult();
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.UserName!) };
                foreach (var role in await users.GetRolesAsync(user)) claims.Add(new Claim(ClaimTypes.Role, role));
                return AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
            }
        }

        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId) => Task.FromResult(Enumerable.Empty<MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey) => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult<IEnumerable<string>>(new[] { "view" });
        }
    }
}
