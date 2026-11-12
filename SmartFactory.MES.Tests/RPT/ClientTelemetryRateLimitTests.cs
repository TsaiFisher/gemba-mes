using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPT
{
    /// <summary>
    /// 遙測限流的單位：未登入以 IP、登入後以使用者＋裝置。
    /// 廠區經 NAT 共用一個 IP，以 IP 計會讓「現場越忙越收不到遙測」（2026-11-09）。
    /// </summary>
    public class ClientTelemetryRateLimitTests
    {
        private static readonly DateTime Now = new(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);
        private static ClientTelemetryBatch Batch(string device) => new(device, "mes-mobile-v47", new());
        private static ClientTelemetrySource User(string id) => new(id, id, "P1", "10.0.0.1", "UA");
        private static readonly ClientTelemetrySource Anonymous = new(null, null, null, "10.0.0.1", "UA");

        private static int Accepted(IMemoryCache cache, int tries, Func<int, (ClientTelemetryBatch, ClientTelemetrySource)> pick)
        {
            var ok = 0;
            for (var i = 0; i < tries; i++)
            {
                var (b, s) = pick(i);
                if (ClientTelemetryService.TryAcquire(cache, b, s, Now)) ok++;
            }
            return ok;
        }

        [Fact]
        public void 同一個IP的多支已登入手機_各自計算_不互相擋()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            // 10 支手機、10 個人，同一個 NAT IP，每支 25 批：全部都要收
            var ok = Accepted(cache, 250, i => (Batch($"dev-{i % 10}"), User($"u{i % 10}")));
            Assert.Equal(250, ok);
        }

        [Fact]
        public void 單一裝置仍有每分鐘上限()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var ok = Accepted(cache, 40, _ => (Batch("dev-1"), User("u1")));
            Assert.Equal(ClientTelemetryService.MaxBatchesPerMinutePerIp, ok);
        }

        [Fact]
        public void 同一人換裝置代號洗限額_被每人總量擋住()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var ok = Accepted(cache, 1000, i => (Batch($"rand-{i}"), User("u1")));
            Assert.Equal(ClientTelemetryService.MaxBatchesPerMinutePerUser, ok);
        }

        [Fact]
        public void 未登入仍以IP計_換裝置代號沒有用()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var ok = Accepted(cache, 100, i => (Batch($"rand-{i}"), Anonymous));
            Assert.Equal(ClientTelemetryService.MaxBatchesPerMinutePerIp, ok);
        }

        [Fact]
        public void 下一分鐘重新計算()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            for (var i = 0; i < 40; i++) ClientTelemetryService.TryAcquire(cache, Batch("d"), User("u1"), Now);
            Assert.True(ClientTelemetryService.TryAcquire(cache, Batch("d"), User("u1"), Now.AddMinutes(1)));
        }
    }
}
