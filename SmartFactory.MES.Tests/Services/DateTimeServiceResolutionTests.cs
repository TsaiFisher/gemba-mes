using Microsoft.Extensions.Logging;
using Moq;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 時區解不開時的行為。這一組全部針對「靜默」——舊實作在時區代碼壞掉時
    /// 不會失敗、不會記錄、畫面也正常，只有時間是錯的。
    /// </summary>
    public class DateTimeServiceResolutionTests
    {
        private const string BadTimeZone = "Invalid/TimeZone";

        /// <summary>
        /// 時區解不開時，<c>ToUtcTime</c> 必須退回預設時區換算，而不是把當地時間直接標成 UTC。
        ///
        /// <para>舊實作對 08:00 會回 08:00Z（整整偏移一個時差存進資料庫）；
        /// 正確行為是以 Asia/Taipei 換算成 00:00Z。</para>
        /// </summary>
        [Fact]
        public void ToUtcTime_UnresolvableTimeZone_DoesNotTreatLocalTimeAsUtc()
        {
            var service = new DateTimeService(UserContextWith(BadTimeZone));

            var result = service.ToUtcTime(new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Unspecified));

            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0), result);
        }

        /// <summary>
        /// 輸入本來就是 UTC 就原樣回傳。這是舊 catch 分支順帶做對的那一半，
        /// 與上一支分開之後仍必須成立。
        /// </summary>
        [Fact]
        public void ToUtcTime_ReturnsUtcInputUnchanged()
        {
            var service = new DateTimeService(UserContextWith("Asia/Taipei"));
            var utc = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);

            Assert.Equal(utc, service.ToUtcTime(utc));
        }

        /// <summary>
        /// Kind=Local 的輸入必須用「使用者時區」換算，不是伺服器時區。
        /// 刻意挑 UTC+14 這種不會是任何伺服器設定的時區，否則兩者相同時斷言分不出來。
        /// </summary>
        [Fact]
        public void ToUtcTime_LocalKind_UsesUserTimeZoneNotServerTimeZone()
        {
            var service = new DateTimeService(UserContextWith("Pacific/Kiritimati"));

            var result = service.ToUtcTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local));

            Assert.Equal(new DateTime(2023, 12, 31, 10, 0, 0), result);
        }

        /// <summary>
        /// 退回預設時區時必須留下一筆 Warning，而且訊息要帶上那個壞掉的代碼——
        /// 不然現場只看得到「日期差一天」，查不到是哪一筆主檔設錯。
        /// </summary>
        [Fact]
        public void Resolve_UnresolvableTimeZone_LogsWarningNamingTheBadId()
        {
            var logger = new Mock<ILogger<DateTimeService>>();
            var service = new DateTimeService(UserContextWith(BadTimeZone), logger.Object);

            service.ToLocalTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            logger.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(BadTimeZone)),
                    It.IsAny<TimeZoneNotFoundException>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        /// <summary>
        /// 「沒設時區」是正常狀態（廠別與個人都留白），走 fallback 但**不**該記 Warning——
        /// 否則每一次時間換算都會刷一筆，真正的設定錯誤就淹沒在裡面了。
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_MissingTimeZone_FallsBackQuietly(string? timeZoneId)
        {
            var logger = new Mock<ILogger<DateTimeService>>();
            var service = new DateTimeService(UserContextWith(timeZoneId), logger.Object);

            var result = service.ToLocalTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(8, result.Hour);
            logger.Verify(
                l => l.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        /// <summary>
        /// <c>GetLocalDate</c> 的參數是給「以廠別時區決定單據日期」用的，必須蓋過使用者時區。
        /// 兩邊給 UTC+14 與 UTC−11，同一個瞬間會落在不同日期，才驗得出真的有用到參數。
        /// </summary>
        [Fact]
        public void GetLocalDate_ExplicitTimeZone_OverridesUserTimeZone()
        {
            var service = new DateTimeService(UserContextWith("Pacific/Midway"));

            var userDate = service.GetLocalDate();
            var plantDate = service.GetLocalDate("Pacific/Kiritimati");

            // UTC−11 與 UTC+14 相差 25 小時，任何一個瞬間都必定跨日。
            Assert.NotEqual(userDate, plantDate);
            Assert.Equal(
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati"))),
                plantDate);
        }

        /// <summary>
        /// 參數給空白等同沒給，回到使用者時區——不是拿空字串去解析。
        /// </summary>
        [Fact]
        public void GetLocalDate_BlankTimeZone_FallsBackToUserTimeZone()
        {
            var logger = new Mock<ILogger<DateTimeService>>();
            var service = new DateTimeService(UserContextWith("Pacific/Kiritimati"), logger.Object);

            Assert.Equal(service.GetLocalDate(), service.GetLocalDate("   "));
            logger.Verify(
                l => l.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        private static IUserContext UserContextWith(string? timeZoneId)
        {
            var mock = new Mock<IUserContext>();
            mock.Setup(x => x.TimeZoneId).Returns(timeZoneId);
            return mock.Object;
        }
    }
}
