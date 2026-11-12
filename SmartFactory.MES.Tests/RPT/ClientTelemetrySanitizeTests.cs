using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPT
{
    /// <summary>
    /// 遙測上報端點允許未登入，清洗規則就是唯一的防線——每一條都要釘住。
    /// </summary>
    public class ClientTelemetrySanitizeTests
    {
        private static readonly DateTime Now = new(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc);
        private static readonly ClientTelemetrySource Authed = new("u1", "alice", "P1", "10.0.0.1", "UA");
        private static readonly ClientTelemetrySource Anonymous = new(null, null, null, "10.0.0.1", "UA");

        private static ClientTelemetryEventDto Ev(string kind, string? message = "m", string? endpoint = null, DateTime? at = null,
            int? count = null, int? queuePending = null) =>
            new(kind, at, "/mobile/mfg/reports.html?dispatchId=D1", message, null, endpoint, count, null, null,
                null, null, null, null, null, queuePending, null, "4g", 80);

        private static List<ClientTelemetryEvent> Run(ClientTelemetrySource src, params ClientTelemetryEventDto[] evs) =>
            ClientTelemetryService.Sanitize(new ClientTelemetryBatch("dev-1", "mes-mobile-v36", evs.ToList()), src, Now);

        [Fact]
        public void 未登入只收錯誤兩種_其他種類丟掉()
        {
            var rows = Run(Anonymous, Ev("Error"), Ev("Rejection"), Ev("ApiStats"), Ev("Queue", queuePending: 3), Ev("Offline"));
            Assert.Equal(new[] { "Error", "Rejection" }, rows.Select(r => r.Kind));
            Assert.All(rows, r => { Assert.Null(r.UserId); Assert.Null(r.PlantId); });
        }

        [Fact]
        public void 未登入每批最多5筆_登入後最多50筆()
        {
            var many = Enumerable.Range(0, 80).Select(_ => Ev("Error")).ToArray();
            Assert.Equal(ClientTelemetryService.MaxEventsAnonymous, Run(Anonymous, many).Count);
            Assert.Equal(ClientTelemetryService.MaxEventsAuthenticated, Run(Authed, many).Count);
        }

        [Fact]
        public void 不認得的種類與沒有種類的丟掉()
        {
            var rows = Run(Authed, Ev("DropTable"), Ev(null!), Ev("error"), Ev("Error"));
            Assert.Single(rows);   // 大小寫也要完全相符：儀表板依字串分組
        }

        [Fact]
        public void 頁面去掉查詢字串_API路徑的編號換成id()
        {
            var r = Assert.Single(Run(Authed, Ev("ApiStats", endpoint: "https://mes.example/api/mobile/sessions/3f2a9c1e-1111-4000-8000-000000000001/complete?x=1")));
            Assert.Equal("/mobile/mfg/reports.html", r.Page);
            Assert.Equal("/api/mobile/sessions/{id}/complete", r.Endpoint);
            Assert.Equal("/api/mobile/machines/{id}/status-event", ClientTelemetryService.NormalizeEndpoint("/api/mobile/machines/M01/status-event"));
        }

        [Fact]
        public void 字串截斷_數字夾在範圍內()
        {
            var r = Assert.Single(Run(Authed, Ev("Error", message: new string('x', 5000), count: -7)));
            Assert.Equal(500, r.Message!.Length);
            Assert.Equal(1, r.Count);
        }

        [Fact]
        public void 手機時間離收到時間超過7天不採信_收到時間一律用伺服器的()
        {
            var rows = Run(Authed, Ev("Error", at: Now.AddDays(-30)), Ev("Error", at: Now.AddMinutes(-5)));
            Assert.Null(rows[0].OccurredAt);
            Assert.Equal(Now.AddMinutes(-5), rows[1].OccurredAt);
            Assert.All(rows, r => Assert.Equal(Now, r.ReceivedAt));
        }

        [Fact]
        public void 登入者與廠別來自伺服器端_不讀手機送來的()
        {
            var r = Assert.Single(Run(Authed, Ev("Queue", queuePending: 2)));
            Assert.Equal(("u1", "alice", "P1", "dev-1", "mes-mobile-v36"), (r.UserId, r.UserName, r.PlantId, r.DeviceId, r.AppVersion));
            Assert.Equal(2, r.QueuePending);
        }
    }
}
