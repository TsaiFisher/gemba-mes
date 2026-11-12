using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機報工／完工／下工（<c>POST reports</c>、<c>PATCH sessions/{id}/complete</c>、<c>PATCH sessions/{id}/stop</c>）。
    ///
    /// <para><b>為什麼要補</b>：這三支是現場最常用的寫入，手機端卻有自己一份 <c>AddReportCoreAsync</c>，
    /// 桌機的 <c>CreateReportControllerTests</c>／<c>CompleteSessionControllerTests</c> 走的是
    /// <c>DispatchController</c>，一支都碰不到這裡。2026-09-28 盤點時兩份已經漂移：
    /// 桌機擋「任一數量為負」（註解寫明混合正負會倒扣 QtyDone），手機只擋「皆 ≤ 0」。</para>
    ///
    /// <para>每個測試實例用自己的一組代號（見 <see cref="MobileMfgTestKit"/>）；專屬廠別 <c>ZZMRP</c>
    /// 不碰 P1／P2 的驗收固定裝置。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileMfgReportingTests
    {
        private const string Plant = "ZZMRP";

        private readonly TestWebApplicationFactory _factory;
        private readonly MobileMfgTestKit.Ids _ids = MobileMfgTestKit.Ids.For("MRP", Guid.NewGuid().ToString("N")[..8]);

        private string DispatchId => _ids.DispatchId;
        private string SessionId  => _ids.DispatchId.Replace("-D-", "-S-");

        public MobileMfgReportingTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        // ── 報工 ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task 報工成功_寫入一筆報工且累加派工完成數()
        {
            var result = await ReportAsync(qtyOk: 5, qtyNg: 1);

            Assert.IsType<OkObjectResult>(result);
            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(15, dispatch.QtyDone);
            var r = Assert.Single(reports);
            Assert.Equal(5, r.QtyOk);
            Assert.Equal(1, r.QtyNg);
        }

        [Fact]
        public async Task 同一個clientId重送_只記一次()
        {
            // 離線佇列在逾時後會重送同一筆；這條保護的是「報工數量被記兩次」
            var clientId = NewClientId();
            Assert.IsType<OkObjectResult>(await ReportAsync(5, 0, clientId));
            Assert.IsType<OkObjectResult>(await ReportAsync(5, 0, clientId));

            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(15, dispatch.QtyDone);
            Assert.Single(reports);
        }

        [Theory]
        [InlineData(-5, 1)]   // 合格為負：穿過「皆 ≤ 0」守門後倒扣派工完成數
        [InlineData(5, -1)]   // 不良為負：寫出一筆負的不良數
        public async Task 任一數量為負_拒絕且不留任何紀錄(int qtyOk, int qtyNg)
        {
            var result = await ReportAsync(qtyOk, qtyNg);

            AssertError(result, "QTY_NEGATIVE");
            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(10, dispatch.QtyDone);
            Assert.Empty(reports);
        }

        [Fact]
        public async Task 合格累計超過派工總量_拒絕()
        {
            AssertError(await ReportAsync(qtyOk: 91, qtyNg: 0), "OVER_REPORT");

            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(10, dispatch.QtyDone);
            Assert.Empty(reports);
        }

        [Fact]
        public async Task 報到剛好滿量_派工自動結案()
        {
            Assert.IsType<OkObjectResult>(await ReportAsync(qtyOk: 90, qtyNg: 0));

            var (dispatch, _) = await LoadAsync();
            Assert.Equal(100, dispatch.QtyDone);
            Assert.Equal(MfgM.DispatchStatus.Closed, dispatch.Status);
        }

        // ── 依單位捨入（CLAUDE.md〈數量欄位 UOM 小數點〉：儲存前依單位四捨五入）──────

        [Theory]
        [InlineData("ZZMRPKG2", 2, 3.456, 3.46)]   // 小數單位：捨到單位位數，不是整數
        [InlineData("ZZMRPPC0", 0, 4.6, 5)]        // 整數單位：四捨五入成整數
        public async Task 報工數量依產品庫存單位捨入(string uom, int dp, double input, double expected)
        {
            await UseProductUomAsync(uom, dp);

            Assert.IsType<OkObjectResult>(await ReportAsync((decimal)input, (decimal)input));

            var (dispatch, reports) = await LoadAsync();
            var r = Assert.Single(reports);
            Assert.Equal((decimal)expected, r.QtyOk);
            Assert.Equal((decimal)expected, r.QtyNg);
            Assert.Equal(10 + (decimal)expected, dispatch.QtyDone);
        }

        [Fact]
        public async Task 整數單位下填0點4_捨入後為0_視為沒填數量()
        {
            // 捨入要在所有數量判定之前：否則 0.4 過了「至少一個大於 0」，寫進去的卻是 0
            await UseProductUomAsync("ZZMRPPC0", 0);

            AssertError(await ReportAsync(0.4m, 0), "QTY_REQUIRED");
            Assert.Empty((await LoadAsync()).Reports);
        }

        // ── 完工（報工＋下工同一交易）────────────────────────────────────────

        [Fact]
        public async Task 完工帶數量_報工並關閉上工紀錄()
        {
            var result = await CompleteAsync(qtyOk: 5, qtyNg: 0);

            Assert.IsType<OkObjectResult>(result);
            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(15, dispatch.QtyDone);
            Assert.Single(reports);
            var session = await LoadSessionAsync();
            Assert.Equal(MfgM.ProductionSessionStatus.Closed, session.Status);
            Assert.NotNull(session.EndAt);
        }

        [Fact]
        public async Task 完工不帶數量_等同純下工不寫報工()
        {
            Assert.IsType<OkObjectResult>(await CompleteAsync(qtyOk: 0, qtyNg: 0));

            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(10, dispatch.QtyDone);
            Assert.Empty(reports);
            Assert.Equal(MfgM.ProductionSessionStatus.Closed, (await LoadSessionAsync()).Status);
        }

        [Theory]
        [InlineData(-5, 1)]
        [InlineData(5, -1)]
        public async Task 完工任一數量為負_拒絕且上工紀錄維持開啟(int qtyOk, int qtyNg)
        {
            AssertError(await CompleteAsync(qtyOk, qtyNg), "QTY_NEGATIVE");

            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(10, dispatch.QtyDone);
            Assert.Empty(reports);
            Assert.Equal(MfgM.ProductionSessionStatus.Open, (await LoadSessionAsync()).Status);
        }

        [Fact]
        public async Task 已下工的上工紀錄再完工_拒絕()
        {
            Assert.IsType<OkObjectResult>(await CompleteAsync(0, 0));

            AssertError(await CompleteAsync(5, 0), "SESSION_NOT_OPEN");
            var (dispatch, reports) = await LoadAsync();
            Assert.Equal(10, dispatch.QtyDone);
            Assert.Empty(reports);
        }

        // ── 下工 ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task 下工_關閉上工紀錄且同一個clientId重送回同一個結果()
        {
            var clientId = NewClientId();
            Assert.IsType<OkObjectResult>(await StopAsync(clientId));
            // 重送若沒走去重，會撞 SESSION_NOT_OPEN——佇列會把它當失敗留在畫面上
            Assert.IsType<OkObjectResult>(await StopAsync(clientId));

            var session = await LoadSessionAsync();
            Assert.Equal(MfgM.ProductionSessionStatus.Closed, session.Status);
            Assert.NotNull(session.EndAt);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static string NewClientId() => MobileMfgTestKit.NewClientId();

        [Fact]
        public async Task 手機報完最後一張派工_工單自動轉完工()
        {
            // 手機的完工檢查原本用 AsNoTracking＋投影讀派工狀態：本次剛設成 Closed 的派工還沒存檔，
            // 讀回資料庫的 InProgress，最後一道在手機報完工時工單永遠停在生產中（桌機早已修過同一個坑）。
            Assert.IsType<OkObjectResult>(await ReportAsync(qtyOk: 90, qtyNg: 0));   // 10 + 90 = 100 達量

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.WorkOrderId == _ids.WoId);
            Assert.Equal(MfgM.WorkOrderStatus.Completed, wo.Status);
        }

        // ── 派工清單帶出「我還開著的上工」（上工頁以此對帳，不再只信 sessionStorage）────

        [Fact]
        public async Task 派工清單_帶出本人開著的上工_下工後消失()
        {
            // 2026-10-05 手機實測：離線下工同步後，上工頁仍拿 sessionStorage 的舊 sessionId
            // 顯示「完工」鈕，一按就是「此上工記錄已下工」。頁面改以這個欄位對帳。
            Assert.Equal(SessionId, await MyOpenSessionIdAsync());

            Assert.IsType<OkObjectResult>(await CompleteAsync(0, 0));

            Assert.Null(await MyOpenSessionIdAsync());
        }

        [Fact]
        public async Task 派工清單_別人開的上工不算本人的()
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var s = await db.ProductionSessions.IgnoreQueryFilters().FirstAsync(x => x.SessionId == SessionId);
                s.OperatorId = "someone-else";
                await db.SaveChangesAsync();
            }

            Assert.Null(await MyOpenSessionIdAsync());
        }

        private async Task<string?> MyOpenSessionIdAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var ok = Assert.IsType<OkObjectResult>(
                await MobileMfgTestKit.MakeController(scope, Plant).GetDispatches(machineId: null));
            // 匿名型別：以序列化後的 JSON 讀，與手機收到的形狀相同
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ok.Value,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            var row = doc.RootElement.GetProperty("data").EnumerateArray()
                .Single(d => d.GetProperty("dispatchId").GetString() == DispatchId);
            var mine = row.GetProperty("myOpenSession");
            return mine.ValueKind == System.Text.Json.JsonValueKind.Null ? null : mine.GetProperty("sessionId").GetString();
        }

        private static void AssertError(IActionResult result, string code)
            => MobileMfgTestKit.AssertError<BadRequestObjectResult>(result, code);

        // ── 斷線補送的發生時間（X-Client-Sent-At，mobile-pwa.md §5.2）──────────────────
        //    驗收清單 W-2／W-4 的伺服器半段：手機按下的時間才是發生時間，不是送達時間。

        private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task 斷線報工排隊5分鐘_報工時間記按下的那一刻_不是送達時間()
        {
            // 手機時鐘快 3 小時也不影響：發生時間＝伺服器現在 −（送出時間 − 按下時間），偏差在相減時抵銷
            var pressed = DateTime.UtcNow.AddHours(3);
            Assert.IsType<OkObjectResult>(await ReportQueuedAsync(pressed, pressed.AddMinutes(5)));

            var r = Assert.Single((await LoadAsync()).Reports);
            AssertNear(DateTime.UtcNow.AddMinutes(-5), r.ReportedAt);
            Assert.True(r.CreatedAt - r.ReportedAt > TimeSpan.FromMinutes(4.5), $"排隊 {r.CreatedAt - r.ReportedAt}");
        }

        [Fact]
        public async Task 送出時間早於按下時間_中途改過時鐘_不產生未來時間()
        {
            var pressed = DateTime.UtcNow;
            Assert.IsType<OkObjectResult>(await ReportQueuedAsync(pressed, pressed.AddMinutes(-20)));
            AssertNear(DateTime.UtcNow, Assert.Single((await LoadAsync()).Reports).ReportedAt);
        }

        [Fact]
        public async Task 排隊超過24小時_拒收且不留紀錄_要改在桌機補登()
        {
            var pressed = DateTime.UtcNow.AddDays(-2);
            AssertError(await ReportQueuedAsync(pressed, pressed.AddHours(25)), "OCCURRED_TOO_OLD");
            Assert.Empty((await LoadAsync()).Reports);
        }

        [Fact]
        public async Task 回推的時間早於上工時間_以上工時間為準()
        {
            var pressed = DateTime.UtcNow;   // 上工是 1 小時前；排隊 2 小時 → 回推到上工之前
            Assert.IsType<OkObjectResult>(await ReportQueuedAsync(pressed, pressed.AddHours(2)));
            var session = await LoadSessionAsync();
            Assert.Equal(session.StartAt, Assert.Single((await LoadAsync()).Reports).ReportedAt, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task 斷線完工排隊31分鐘後確認送出_下工時間是按下的那一刻_工時不多算()
        {
            var pressed = DateTime.UtcNow;
            using (var scope = _factory.Services.CreateScope())
            {
                var c = MobileMfgTestKit.MakeController(scope, Plant);
                c.ControllerContext.HttpContext.Request.Headers[MobileOccurredAt.SentAtHeader] = pressed.AddMinutes(31).ToString("O");
                Assert.IsType<OkObjectResult>(await c.CompleteSession(SessionId, new SessionCompleteRequest(NewClientId(), pressed, 0, 0)));
            }
            var s = await LoadSessionAsync();
            AssertNear(DateTime.UtcNow.AddMinutes(-31), s.EndAt!.Value);
        }

        private async Task<IActionResult> ReportQueuedAsync(DateTime pressedPhoneClock, DateTime sentPhoneClock)
        {
            using var scope = _factory.Services.CreateScope();
            var c = MobileMfgTestKit.MakeController(scope, Plant);
            c.ControllerContext.HttpContext.Request.Headers[MobileOccurredAt.SentAtHeader] = sentPhoneClock.ToString("O");
            return await c.CreateReport(new ReportCreateRequest(SessionId, 1, 0, ContainerId: null,
                ClientId: NewClientId(), ClientTimestamp: pressedPhoneClock));
        }

        private static void AssertNear(DateTime expectedUtc, DateTime actual)
        {
            var a = actual.Kind == DateTimeKind.Local ? actual.ToUniversalTime() : DateTime.SpecifyKind(actual, DateTimeKind.Utc);
            Assert.True((a - expectedUtc).Duration() < Tolerance, $"預期約 {expectedUtc:O}，實際 {a:O}");
        }

        private async Task<IActionResult> ReportAsync(decimal qtyOk, decimal qtyNg, string? clientId = null)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).CreateReport(new ReportCreateRequest(
                SessionId, qtyOk, qtyNg, ContainerId: null,
                ClientId: clientId ?? NewClientId(), ClientTimestamp: DateTime.UtcNow));
        }

        private async Task<IActionResult> CompleteAsync(int qtyOk, int qtyNg)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).CompleteSession(SessionId, new SessionCompleteRequest(
                NewClientId(), DateTime.UtcNow, qtyOk, qtyNg));
        }

        private async Task<IActionResult> StopAsync(string clientId)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).StopSession(SessionId, new SetupDoneRequest(clientId, DateTime.UtcNow));
        }

        /// <summary>把本測試工單的產品換成一個以 <paramref name="uom"/> 為庫存單位的產品。</summary>
        private async Task UseProductUomAsync(string uom, int dp)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 單位是全域主檔：測試專用代碼，已存在就沿用（小數位數固定，不會與別的測試衝突）
            if (!await db.Uoms.AnyAsync(u => u.UomCode == uom))
                db.Uoms.Add(new Uom { UomCode = uom, UomName = uom, DecimalPlaces = dp });
            var productCode = _ids.WoId.Replace("-WO-", "-P-");
            db.Products.Add(new Product { ProductCode = productCode, ProductName = productCode, StockUom = uom });
            var wo = await db.WorkOrders.IgnoreQueryFilters().FirstAsync(w => w.WorkOrderId == _ids.WoId);
            wo.ProductCode = productCode;
            await db.SaveChangesAsync();
            // app 共用一份記憶體快取：不清掉會讀到新增單位之前的對照表，被當成 0 位小數
            scope.ServiceProvider.GetRequiredService<IUomService>().InvalidateDecimalPlacesCache();
        }

        private async Task<(MfgM.Dispatch Dispatch, List<MfgM.ProductionReport> Reports)> LoadAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var d = await db.Dispatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.DispatchId == DispatchId);
            var r = await db.ProductionReports.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.DispatchId == DispatchId).ToListAsync();
            return (d, r);
        }

        private async Task<MfgM.ProductionSession> LoadSessionAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking().FirstAsync(s => s.SessionId == SessionId);
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            MobileMfgTestKit.SeedDispatch(db, Plant, _ids, MfgM.DispatchStatus.InProgress, quantity: 100, qtyDone: 10);
            db.ProductionSessions.Add(new MfgM.ProductionSession
            {
                SessionId = SessionId, DispatchId = DispatchId, WorkOrderId = _ids.WoId,
                Status = MfgM.ProductionSessionStatus.Open, StartAt = DateTime.UtcNow.AddHours(-1),
                OperatorId = "admin", MachineCode = MobileMfgTestKit.Machine(Plant), PlantId = Plant,
            });
            db.SaveChanges();
        }
    }
}
