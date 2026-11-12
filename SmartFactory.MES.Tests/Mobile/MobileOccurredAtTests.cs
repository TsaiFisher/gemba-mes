using SmartFactory.MES.Api.Mobile;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 斷線補送的發生時間換算（<see cref="MobileOccurredAt"/>）。端點層的行為在整合測試
    /// MobileOccurredAtTests；這裡只鎖住換算本身的邊界——尤其是「絕不產生未來時間」。
    /// </summary>
    public class MobileOccurredAtTests
    {
        private static readonly DateTime ServerNow = new(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void 排隊時間由同一支手機的兩個時間相減_手機時鐘偏差抵銷()
        {
            // 手機快 1 小時：排入 11:00（手機）、送出 11:20（手機）→ 排隊 20 分鐘
            var phoneEnqueued = ServerNow.AddHours(1).AddMinutes(-20);
            var phoneSent     = ServerNow.AddHours(1);

            var at = MobileOccurredAt.Resolve(phoneEnqueued, phoneSent.ToString("O"), ServerNow, out var queued);

            Assert.Equal(TimeSpan.FromMinutes(20), queued);
            Assert.Equal(ServerNow.AddMinutes(-20), at);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-date")]
        public void 沒帶或看不懂送出時間_視為沒有排隊_記伺服器現在(string? header)
        {
            var at = MobileOccurredAt.Resolve(ServerNow.AddHours(-5), header, ServerNow, out var queued);

            Assert.Equal(TimeSpan.Zero, queued);
            Assert.Equal(ServerNow, at);
        }

        [Fact]
        public void 送出時間早於排入時間_手機中途調過時鐘_不產生未來時間()
        {
            var at = MobileOccurredAt.Resolve(ServerNow, ServerNow.AddMinutes(-30).ToString("O"), ServerNow, out _);

            Assert.Equal(ServerNow, at);
        }

        [Fact]
        public void 超過上限_回null交由端點拒收()
        {
            var at = MobileOccurredAt.Resolve(ServerNow.AddHours(-25), ServerNow.ToString("O"), ServerNow, out var queued);

            Assert.Null(at);
            Assert.True(queued > MobileOccurredAt.MaxBackdate);
        }
    }
}
