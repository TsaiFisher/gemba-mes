using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Timezone
{
    /// <summary>
    /// 跨時區測試的共用組裝件。
    ///
    /// 兩種用法，對應兩種不同的測試意圖：
    ///
    /// <list type="bullet">
    /// <item><b><see cref="FixedLocalDate"/></b>（mock）——「這支程式有沒有把業務日交給
    /// <see cref="IDateTimeService"/> 決定」。斷言單據日期等於那個固定值即可；
    /// 若程式改成直接讀伺服器時鐘，日期就不會是這個值，測試會紅。</item>
    ///
    /// <item><b><see cref="RealIn"/></b>（真實實作）——「換一個時區，結果真的會跟著換嗎」。
    /// mock 永遠證明不了這件事：mock 回什麼就是什麼，時區換算邏輯根本沒被執行到。</item>
    /// </list>
    ///
    /// 兩個極端時區的選擇是刻意的：<see cref="FarEast"/>（UTC+14）與 <see cref="FarWest"/>
    /// （UTC−11）相距 25 小時，**任何時刻**兩者的當地日期都必然不同。用台北 vs 紐約
    /// 則只在一天中的某些時段才會跨日——測試會變成早上綠、晚上紅的間歇性失敗。
    ///
    /// ⚠️ 「必然不同」不等於「必然差一天」：因為是 25 小時而不是 24，每天有整整一小時
    /// （UTC 10:00–11:00）差距是 **2** 天。斷言要寫成範圍，寫死 == 1 反而製造出
    /// 本段一開始想避免的那種間歇性失敗（2026-08-18 實際踩到）。
    /// </summary>
    internal static class TimezoneTestKit
    {
        /// <summary>UTC+14，全球最早進入新的一天（Line Islands，無日光節約）。</summary>
        internal const string FarEast = "Pacific/Kiritimati";

        /// <summary>UTC−11，全球最晚（Niue，無日光節約）。與 <see cref="FarEast"/> 相距 25 小時。</summary>
        internal const string FarWest = "Pacific/Niue";

        internal const string Taipei = "Asia/Taipei";

        /// <summary>回傳一個把業務日固定成 <paramref name="date"/> 的 mock。</summary>
        internal static Mock<IDateTimeService> FixedLocalDate(DateOnly date)
        {
            var mock = new Mock<IDateTimeService>();
            mock.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(date);
            mock.Setup(d => d.ToLocalTime(It.IsAny<DateTime>())).Returns<DateTime>(dt => dt);
            mock.Setup(d => d.Now).Returns(date.ToDateTime(TimeOnly.MinValue));
            return mock;
        }

        /// <summary>
        /// 回傳**真實**的 <see cref="DateTimeService"/>，其 <see cref="IUserContext.TimeZoneId"/>
        /// 為 <paramref name="timeZoneId"/>（正式環境即 <c>Plant.DefaultTimeZone</c>）。
        /// </summary>
        internal static IDateTimeService RealIn(string timeZoneId)
        {
            var user = new Mock<IUserContext>();
            user.Setup(u => u.TimeZoneId).Returns(timeZoneId);
            return new DateTimeService(user.Object);
        }

        /// <summary>指定時區的「現在」當地日期——測試的期望值來源。</summary>
        internal static DateOnly TodayIn(string timeZoneId) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }
}
