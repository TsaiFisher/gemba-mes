using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPS.Models;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 由排程設定算出下一次執行的 UTC 時刻（§7.2 定案 #13）。
///
/// <para><b>純函式，沒有任何相依</b>——刻意不注入 <c>IDateTimeService</c>：
/// 那個服務相依 <c>IUserContext</c>，而派工器是 <c>BackgroundService</c>，沒有使用者上下文。
/// 時區以參數傳入，讓這段邏輯可以在沒有 scope 的地方呼叫，也讓它完全可測。</para>
///
/// <para>兩個必須寫進實作的邊界條件（規格明列，都是「不處理就會靜默少一次報表」）：</para>
/// <list type="number">
///   <item><b>Monthly 的 29/30/31 號</b>：2 月沒有 31 號 → **取當月最後一日**，
///         不是跳過該月。跳過的話二月份靜默沒有報表，而且沒有任何錯誤。</item>
///   <item><b>一律以 UTC 落庫</b>，計算時才換算時區。直接用 <c>DateTime.Now</c>
///         會讓跨時區部署在錯誤的當地時間送出。</item>
/// </list>
/// </summary>
public static class ReportScheduleCalculator
{
    /// <summary>時區代碼查不到時的退路。與 <c>DateTimeService</c> 的行為一致。</summary>
    public const string FallbackTimeZoneId = "Asia/Taipei";

    /// <summary>
    /// 算出「嚴格晚於 <paramref name="afterUtc"/>」的下一次執行時刻（UTC）。
    ///
    /// <para>嚴格大於是必要的：用「大於等於」的話，剛執行完就以同一個時刻重算，
    /// 會得到同一個 <c>NextRunAt</c>，於是同一份報表在同一分鐘內被反覆執行。</para>
    /// </summary>
    /// <returns><paramref name="definition"/> 不是排程（<c>ScheduleKind</c> 為 null）時回 <c>null</c>。</returns>
    public static DateTime? ComputeNextRunUtc(
        ReportDefinition definition, DateTime afterUtc, string? timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.ScheduleKind is not { } kind) return null;

        var tz = ResolveTimeZone(timeZoneId);

        // afterUtc 可能被呼叫端以 Unspecified 傳進來（例如從 DB 讀出的欄位）。
        // 不校正 Kind 的話 ConvertTimeFromUtc 會拋，而那個例外在背景服務裡只會變成一行日誌。
        if (afterUtc.Kind != DateTimeKind.Utc)
            afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);

        var hour   = Clamp(definition.ScheduleHour ?? 0, 0, 23);
        var minute = Clamp(definition.ScheduleMinute ?? 0, 0, 59);

        var localAfter = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, tz);

        var localNext = kind switch
        {
            ReportScheduleKind.Daily   => NextDaily(localAfter, hour, minute),
            ReportScheduleKind.Weekly  => NextWeekly(localAfter, hour, minute,
                                              Clamp(definition.ScheduleDayOfWeek ?? 1, 0, 6)),
            ReportScheduleKind.Monthly => NextMonthly(localAfter, hour, minute,
                                              Clamp(definition.ScheduleDayOfMonth ?? 1, 1, 31)),
            _ => throw new InvalidOperationException(PzText.Get("Rps_UnknownScheduleFrequencyFmt", kind)),
        };

        return ToUtc(localNext, tz);
    }

    private static DateTime NextDaily(DateTime localAfter, int hour, int minute)
    {
        var candidate = localAfter.Date.AddHours(hour).AddMinutes(minute);
        return candidate > localAfter ? candidate : candidate.AddDays(1);
    }

    private static DateTime NextWeekly(DateTime localAfter, int hour, int minute, int dayOfWeek)
    {
        var candidate = localAfter.Date.AddHours(hour).AddMinutes(minute);

        // 先移到本週的目標星期幾，再視是否已過決定要不要 +7。
        var delta = ((dayOfWeek - (int)candidate.DayOfWeek) + 7) % 7;
        candidate = candidate.AddDays(delta);

        return candidate > localAfter ? candidate : candidate.AddDays(7);
    }

    private static DateTime NextMonthly(DateTime localAfter, int hour, int minute, int dayOfMonth)
    {
        var candidate = InMonth(localAfter.Year, localAfter.Month, dayOfMonth, hour, minute);
        if (candidate > localAfter) return candidate;

        var nextMonth = new DateTime(localAfter.Year, localAfter.Month, 1).AddMonths(1);
        return InMonth(nextMonth.Year, nextMonth.Month, dayOfMonth, hour, minute);
    }

    /// <summary>指定「幾號」超過該月天數時取當月最後一日——不是跳過該月。</summary>
    private static DateTime InMonth(int year, int month, int dayOfMonth, int hour, int minute)
    {
        var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// 當地時間換回 UTC。夏令時間的兩個惡性情況都要處理，否則 <c>ConvertTimeToUtc</c> 會拋：
    /// 不存在的時刻（時鐘往前跳）往後推一小時，重複的時刻取較早的那一次。
    /// 台灣沒有夏令時間，但這段邏輯會被跨時區部署用到。
    /// </summary>
    private static DateTime ToUtc(DateTime localTime, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        if (tz.IsAmbiguousTime(unspecified))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(unspecified);
            var earliest = offsets.Max();   // 位移最大 ＝ 當地同一時刻中較早發生的那一次
            return DateTime.SpecifyKind(unspecified - earliest, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (TimeZoneNotFoundException)  { /* 落到下面的預設值 */ }
            catch (InvalidTimeZoneException)   { /* 同上 */ }
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById(FallbackTimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
