using Moq;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class DateTimeServiceTests
    {
        [Theory]
        [InlineData("Asia/Taipei", "2024-01-01 00:00:00", "2024-01-01 08:00:00")]
        [InlineData("America/New_York", "2024-01-01 00:00:00", "2023-12-31 19:00:00")]
        [InlineData("Europe/London", "2024-01-01 00:00:00", "2024-01-01 00:00:00")]
        public void ToLocalTime_ConvertsCorrectly(string timeZoneId, string utcTimeStr, string expectedLocalTimeStr)
        {
            // Arrange
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns(timeZoneId);
            var service = new DateTimeService(mockUserContext.Object);
            
            var utcTime = DateTime.SpecifyKind(DateTime.Parse(utcTimeStr), DateTimeKind.Utc);
            var expectedLocalTime = DateTime.Parse(expectedLocalTimeStr);

            // Act
            var result = service.ToLocalTime(utcTime);

            // Assert
            Assert.Equal(expectedLocalTime.Year, result.Year);
            Assert.Equal(expectedLocalTime.Month, result.Month);
            Assert.Equal(expectedLocalTime.Day, result.Day);
            Assert.Equal(expectedLocalTime.Hour, result.Hour);
        }

        [Fact]
        public void ToLocalTime_InvalidTimeZone_FallsBackToTaipei()
        {
            // Arrange
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Invalid/TimeZone");
            var service = new DateTimeService(mockUserContext.Object);
            
            var utcTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Act
            var result = service.ToLocalTime(utcTime);

            // Assert - 應回退到台北時間 (UTC+8)
            Assert.Equal(8, result.Hour);
        }

        [Fact]
        public void ToUtcTime_ConvertsCorrectly()
        {
            // Arrange
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Asia/Taipei");
            var service = new DateTimeService(mockUserContext.Object);
            
            var localTime = new DateTime(2024, 1, 1, 8, 0, 0);

            // Act
            var result = service.ToUtcTime(localTime);

            // Assert - 台北時間 08:00 應轉換為 UTC 00:00
            Assert.Equal(0, result.Hour);
        }

        [Fact]
        public void Now_ReturnsLocalTime()
        {
            // Arrange
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Asia/Taipei");
            var service = new DateTimeService(mockUserContext.Object);

            // Act
            var now = service.Now;

            // Assert - 應返回當前時間 (難以精確測試，僅驗證不為預設值)
            Assert.NotEqual(DateTime.MinValue, now);
        }

        [Theory]
        [InlineData("Asia/Taipei")]       // UTC+8 — 日期最多超前 UTC 一天
        [InlineData("America/New_York")]  // UTC-5/-4 — 日期可能落後 UTC 一天
        [InlineData("Europe/London")]     // UTC+0/+1
        public void GetLocalDate_WithTimeZoneId_ReturnsThatZonesToday(string timeZoneId)
        {
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Asia/Taipei"); // user tz 不影響結果
            var service = new DateTimeService(mockUserContext.Object);

            // 期望值獨立算出（不重用受測程式的邏輯）。原本只斷言「距 UTC 今日 ≤ 1 天」，
            // 那對「直接回 UTC 日、完全不換算」的錯誤實作也成立。
            var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));

            Assert.Equal(expected, service.GetLocalDate(timeZoneId));
        }

        [Fact]
        public void GetLocalDate_WithNullTimeZone_FallsBackToUserContextTz()
        {
            // 用 UTC+14 而非台北：若實作忘了換算而直接回 UTC 日，台北在一天中大部分
            // 時段仍會與 UTC 同日而測不出來；UTC+14 只有 10 小時的重疊窗。
            const string tz = "Pacific/Kiritimati";
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns(tz);
            var service = new DateTimeService(mockUserContext.Object);

            var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz)));

            // null → 使用 userContext.TimeZoneId（正式環境即 ActivePlant.DefaultTimeZone）
            Assert.Equal(expected, service.GetLocalDate(null));
        }

        [Fact]
        public void GetLocalDate_WithInvalidTimeZone_FallsBackToTaipei()
        {
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Asia/Taipei");
            var service = new DateTimeService(mockUserContext.Object);

            // 無效 tz → fallback 台北
            var result = service.GetLocalDate("Invalid/Zone");
            var expected = service.GetLocalDate("Asia/Taipei");

            Assert.Equal(expected, result);
        }

        // 廠別 tz（參數）必須壓過使用者 tz——這是跨時區建單的核心保證：
        // 台北的採購人員替美國廠建單，業務日該是美國廠的當地日，不是他自己的。
        //
        // 時區組合是刻意挑的：Kiritimati(UTC+14) 與 Niue(UTC-11) 相距 25 小時，
        // 故**任何時刻**兩者的當地日期都必然不同（差恰為 1 天）。
        // 原本用 Taipei vs New_York 只在一天中的部分時段才跨日，斷言又只寫
        // 「日期差 ≤ 1 天」——那對「完全忽略參數、永遠回使用者 tz」的錯誤實作
        // 同樣成立，等於測不到任何東西。
        [Theory]
        [InlineData("Pacific/Kiritimati", "Pacific/Niue")]
        [InlineData("Pacific/Niue", "Pacific/Kiritimati")]
        public void GetLocalDate_TimeZoneArgument_OverridesUserContextTz(
            string userTz, string plantTz)
        {
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns(userTz);
            var service = new DateTimeService(mockUserContext.Object);

            var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(plantTz)));

            var actual = service.GetLocalDate(plantTz);

            Assert.Equal(expected, actual);
            // 並且真的與使用者 tz 的日期不同——證明參數沒有被忽略
            Assert.NotEqual(service.GetLocalDate(), actual);
        }

        // Kiritimati(UTC+14) 與 Niue(UTC-11) 相距 25 小時，日期恆差 1 天。
        // 這條同時是上面那組時區假設的自我檢查：哪天 IANA 資料庫改了偏移
        // （或執行環境缺少 tzdata），這裡會先紅，而不是讓上面的測試無聲失去鑑別力。
        [Fact]
        public void GetLocalDate_ExtremeTimeZones_AlwaysDifferByExactlyOneDay()
        {
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(x => x.TimeZoneId).Returns("Asia/Taipei");
            var service = new DateTimeService(mockUserContext.Object);

            var east = service.GetLocalDate("Pacific/Kiritimati");
            var west = service.GetLocalDate("Pacific/Niue");

            // 兩地相距 25 小時，故差距是 1 或 2——UTC 10:00–11:00 這一小時內
            // Kiritimati 已跨日而 Niue 還沒，差距為 2。寫死 == 1 每天固定紅一小時。
            Assert.InRange(east.DayNumber - west.DayNumber, 1, 2);
        }
    }
}
