using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 斷線補送的發生時間（<see cref="MobileOccurredAt"/>，mobile-pwa.md §5.2 斷線保護）。
    ///
    /// <para><b>為什麼要補</b>：2026-10-10 盤點時發現手機佇列每筆都帶了 clientTimestamp，
    /// 伺服器卻只拿來去重，下工／報工一律記成送達當下——斷線 25 分鐘就多算 25 分鐘工時，
    /// 人工／機台成本與稼動率跟著偏高，而且資料看起來完全正常。</para>
    ///
    /// <para>手機時鐘不可信，所以測試刻意讓手機時鐘快 7 分鐘：發生時間要由
    /// 「送出時間 − 排入時間」（同一支手機的時鐘）回推，偏差在相減時抵銷。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileOccurredAtTests
    {
        private const string Plant = "ZZMRP";
        private static readonly TimeSpan PhoneClockSkew = TimeSpan.FromMinutes(7);
        private static readonly TimeSpan Tolerance      = TimeSpan.FromSeconds(30);

        private readonly TestWebApplicationFactory _factory;
        private readonly MobileMfgTestKit.Ids _ids = MobileMfgTestKit.Ids.For("MOA", Guid.NewGuid().ToString("N")[..8]);

        private string DispatchId => _ids.DispatchId;
        private string SessionId  => _ids.DispatchId.Replace("-D-", "-S-");

        public MobileOccurredAtTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed(sessionStartedAgo: TimeSpan.FromHours(1));
        }

        [Fact]
        public async Task 下工補送_記發生時間而不是送達時間_手機時鐘偏差不影響()
        {
            var result = await StopAsync(queuedFor: TimeSpan.FromMinutes(25));

            Assert.IsType<OkObjectResult>(result);
            var session = await LoadSessionAsync();
            AssertNear(DateTime.UtcNow - TimeSpan.FromMinutes(25), session.EndAt!.Value);
        }

        [Fact]
        public async Task 報工補送_報工時間記發生時間()
        {
            var result = await ReportAsync(queuedFor: TimeSpan.FromMinutes(12));

            Assert.IsType<OkObjectResult>(result);
            var report = Assert.Single(await LoadReportsAsync());
            AssertNear(DateTime.UtcNow - TimeSpan.FromMinutes(12), report.ReportedAt);
        }

        [Fact]
        public async Task 完工並報工補送_報工與下工都記發生時間()
        {
            var result = await CompleteAsync(queuedFor: TimeSpan.FromMinutes(18), qtyOk: 3);

            Assert.IsType<OkObjectResult>(result);
            var expected = DateTime.UtcNow - TimeSpan.FromMinutes(18);
            AssertNear(expected, (await LoadSessionAsync()).EndAt!.Value);
            AssertNear(expected, Assert.Single(await LoadReportsAsync()).ReportedAt);
        }

        [Fact]
        public async Task 直接連線送出_沒帶送出時間_照舊記送達時間_不採信手機時鐘()
        {
            // 手機時鐘慢了 3 小時：若直接採用 clientTimestamp，工時會少算 3 小時
            using var scope = _factory.Services.CreateScope();
            var result = await MobileMfgTestKit.MakeController(scope, Plant).StopSession(SessionId,
                new SetupDoneRequest(MobileMfgTestKit.NewClientId(), DateTime.UtcNow.AddHours(-3)));

            Assert.IsType<OkObjectResult>(result);
            AssertNear(DateTime.UtcNow, (await LoadSessionAsync()).EndAt!.Value);
        }

        [Fact]
        public async Task 回推早於上工時間_以上工時間為準_不產生負工時()
        {
            var startAt = (await LoadSessionAsync()).StartAt;

            Assert.IsType<OkObjectResult>(await StopAsync(queuedFor: TimeSpan.FromHours(3)));

            Assert.Equal(startAt, (await LoadSessionAsync()).EndAt);
        }

        [Fact]
        public async Task 排隊超過24小時_拒收且上工紀錄維持開啟()
        {
            var result = await StopAsync(queuedFor: TimeSpan.FromHours(25));

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(result, "OCCURRED_TOO_OLD");
            Assert.Equal(MfgM.ProductionSessionStatus.Open, (await LoadSessionAsync()).Status);
        }

        [Fact]
        public async Task 回推到已關帳期間的日期_拒收()
        {
            // 回推到廠別當地「昨天」：排隊時間＝今天已過的時間＋5 分鐘
            var tz        = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            var localNow  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var queuedFor = localNow.TimeOfDay + TimeSpan.FromMinutes(5);
            var yesterday = DateOnly.FromDateTime(localNow).AddDays(-1);
            await SetSessionStartAsync(TimeSpan.FromHours(23.9));   // 上工在更早之前，避免被上工時間夾住

            var periodId = $"M{Guid.NewGuid():N}"[..6];   // 欄位 varchar(6)；本測試專屬廠別，用完即刪
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.AccountingPeriods.Add(new AccountingPeriod
                {
                    PeriodId = periodId, PlantId = Plant, Year = yesterday.Year, Month = yesterday.Month,
                    StartDate = yesterday, EndDate = yesterday,
                    Status = PeriodStatus.Closed, ClosedAt = DateTime.UtcNow, ClosedBy = "test",
                });
                await db.SaveChangesAsync();
            }
            try
            {
                var result = await StopAsync(queuedFor);

                MobileMfgTestKit.AssertError<BadRequestObjectResult>(result, "PERIOD_CLOSED");
                Assert.Equal(MfgM.ProductionSessionStatus.Open, (await LoadSessionAsync()).Status);
            }
            finally
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.AccountingPeriods.IgnoreQueryFilters()
                    .Where(p => p.PeriodId == periodId).ExecuteDeleteAsync();
            }
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static void AssertNear(DateTime expected, DateTime actual)
            => Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

        /// <summary>模擬佇列補送：排入與送出都用「快 7 分鐘」的手機時鐘。</summary>
        private (DateTime ClientTimestamp, string SentAt) PhoneClock(TimeSpan queuedFor)
        {
            var phoneNow = DateTime.UtcNow + PhoneClockSkew;
            return (phoneNow - queuedFor, phoneNow.ToString("O"));
        }

        private MobileMfgController Controller(IServiceScope scope, string sentAt)
        {
            var c = MobileMfgTestKit.MakeController(scope, Plant);
            c.ControllerContext.HttpContext.Request.Headers[MobileOccurredAt.SentAtHeader] = sentAt;
            return c;
        }

        private async Task<IActionResult> StopAsync(TimeSpan queuedFor)
        {
            var (ts, sentAt) = PhoneClock(queuedFor);
            using var scope = _factory.Services.CreateScope();
            return await Controller(scope, sentAt).StopSession(SessionId,
                new SetupDoneRequest(MobileMfgTestKit.NewClientId(), ts));
        }

        private async Task<IActionResult> ReportAsync(TimeSpan queuedFor)
        {
            var (ts, sentAt) = PhoneClock(queuedFor);
            using var scope = _factory.Services.CreateScope();
            return await Controller(scope, sentAt).CreateReport(new ReportCreateRequest(
                SessionId, 5, 0, ContainerId: null, ClientId: MobileMfgTestKit.NewClientId(), ClientTimestamp: ts));
        }

        private async Task<IActionResult> CompleteAsync(TimeSpan queuedFor, int qtyOk)
        {
            var (ts, sentAt) = PhoneClock(queuedFor);
            using var scope = _factory.Services.CreateScope();
            return await Controller(scope, sentAt).CompleteSession(SessionId,
                new SessionCompleteRequest(MobileMfgTestKit.NewClientId(), ts, qtyOk, 0));
        }

        private async Task<MfgM.ProductionSession> LoadSessionAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking().FirstAsync(s => s.SessionId == SessionId);
        }

        private async Task<List<MfgM.ProductionReport>> LoadReportsAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionReports.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.DispatchId == DispatchId).ToListAsync();
        }

        private async Task SetSessionStartAsync(TimeSpan ago)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var s = await db.ProductionSessions.IgnoreQueryFilters().FirstAsync(x => x.SessionId == SessionId);
            s.StartAt = DateTime.UtcNow - ago;
            await db.SaveChangesAsync();
        }

        private void Seed(TimeSpan sessionStartedAgo)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            MobileMfgTestKit.SeedDispatch(db, Plant, _ids, MfgM.DispatchStatus.InProgress, quantity: 100, qtyDone: 10);
            db.ProductionSessions.Add(new MfgM.ProductionSession
            {
                SessionId = SessionId, DispatchId = DispatchId, WorkOrderId = _ids.WoId,
                Status = MfgM.ProductionSessionStatus.Open, StartAt = DateTime.UtcNow - sessionStartedAgo,
                OperatorId = "admin", MachineCode = "MOBKIT-M", PlantId = Plant,
            });
            db.SaveChanges();
        }
    }
}
