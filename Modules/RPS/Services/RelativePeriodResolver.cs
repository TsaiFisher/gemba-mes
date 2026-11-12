using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 把 <see cref="RelativePeriod"/> 解算成絕對區間（§5.2.1）。
/// </summary>
public interface IRelativePeriodResolver
{
    /// <summary>
    /// 解算單一相對期間。
    /// </summary>
    /// <param name="timeZoneId">
    /// 明確時區。**排程路徑必須傳**——不傳會退回 <c>IUserContext.TimeZoneId</c>，
    /// 而背景服務沒有 HTTP 上下文，那個值是 null（最終退到 Asia/Taipei）。
    /// 廠別跨時區時這會讓「昨天」差一天。
    /// </param>
    Task<DateRange> ResolveAsync(RelativePeriod period, string? timeZoneId = null, CancellationToken ct = default);

    /// <summary>
    /// 依請求解出實際查詢區間：<see cref="ReportRequestBase.Period"/> 優先，
    /// 其次 <see cref="ReportRequestBase.AbsolutePeriod"/>。兩者皆無或皆有 → 擲例外。
    /// </summary>
    /// <param name="timeZoneId">
    /// 不傳（或傳 <c>null</c>）時**退回 <see cref="ReportRequestBase.TimeZoneId"/>**，
    /// 再退回 <c>IUserContext.TimeZoneId</c>。這個中間層是 2026-09-15 補的：
    /// 在那之前八個呼叫端**一個都沒有傳時區**（全是 <c>ResolveAsync(request, ct: ct)</c>），
    /// 於是 <c>ReportRequestBase.TimeZoneId</c> 在相對期間這條路上是個**沒有讀取點的屬性**，
    /// 排程的「昨天」一律解成 Asia/Taipei 的昨天。見 manual-test-plan §10.3 TZ-3。
    /// </param>
    Task<DateRange> ResolveAsync(ReportRequestBase request, string? timeZoneId = null, CancellationToken ct = default);

    /// <summary>
    /// 當地日期 <paramref name="day"/> 的 00:00，換成 UTC 時刻（Kind=Utc）。
    /// 給比對 <c>timestamp with time zone</c> 欄位的工具用：區間是當地日期，欄位是時刻。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不可用 <c>day.ToDateTime(TimeOnly.MinValue)</c> 代替。那是 Kind=Unspecified，
    /// Npgsql 直接拒寫（"Cannot write DateTime with Kind=Unspecified"）；InMemory 測試不會報，
    /// 2026-10-07 人工驗收 TZ-3 才在排程「立即執行」撞到。就算硬標成 Utc 也錯——
    /// 那是 UTC 的午夜，紐約廠的「昨天」會偏 4～5 小時。
    /// </remarks>
    /// <param name="timeZoneId">null 時退回 <c>IUserContext.TimeZoneId</c>，與 ResolveAsync(request) 同一套退回順序。</param>
    DateTime LocalDayStartUtc(DateOnly day, string? timeZoneId);
}

/// <inheritdoc />
public class RelativePeriodResolver : IRelativePeriodResolver
{
    private readonly IDateTimeService     _dateTime;
    private readonly ApplicationDbContext _db;

    public RelativePeriodResolver(IDateTimeService dateTime, ApplicationDbContext db)
    {
        _dateTime = dateTime;
        _db       = db;
    }

    /// <summary>
    /// 週起始固定為**星期一**（ISO 8601），刻意**不**讀
    /// <c>CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek</c>：
    /// 那會讓同一個「本週」在畫面（zh-TW，週日起）與排程（行程預設語系）解出不同區間，
    /// 正是 §5.5 T7 要防的那類文化相依漂移。
    /// </summary>
    private const DayOfWeek WeekStart = DayOfWeek.Monday;

    /// <inheritdoc />
    public DateTime LocalDayStartUtc(DateOnly day, string? timeZoneId)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);   // Kind=Unspecified

        if (string.IsNullOrWhiteSpace(timeZoneId))
            return DateTime.SpecifyKind(_dateTime.ToUtcTime(local), DateTimeKind.Utc);

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 與 DateTimeService.GetLocalDate 同一個退路：區間是用它解出來的，界線必須跟它同一個時區。
            tz = TimeZoneInfo.FindSystemTimeZoneById(DateTimeService.FallbackTimeZoneId);
        }

        // 午夜實施日光節約的時區（例如部分南美）當天 00:00 不存在，ConvertTimeToUtc 會擲例外；
        // 那一天實際上從 01:00 開始。
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public async Task<DateRange> ResolveAsync(
        RelativePeriod period, string? timeZoneId = null, CancellationToken ct = default)
    {
        var today = _dateTime.GetLocalDate(timeZoneId);

        return period switch
        {
            RelativePeriod.Today          => new DateRange(today, today),
            RelativePeriod.Yesterday      => new DateRange(today.AddDays(-1), today.AddDays(-1)),

            RelativePeriod.ThisWeek       => WeekOf(today),
            RelativePeriod.LastWeek       => WeekOf(today.AddDays(-7)),

            RelativePeriod.ThisMonth      => MonthOf(today),
            RelativePeriod.LastMonth      => MonthOf(FirstOfMonth(today).AddDays(-1)),

            RelativePeriod.ThisQuarter    => QuarterOf(today),
            RelativePeriod.LastQuarter    => QuarterOf(QuarterOf(today).From.AddDays(-1)),

            RelativePeriod.ThisYear       => YearOf(today),
            RelativePeriod.LastYear       => YearOf(new DateOnly(today.Year - 1, 1, 1)),

            // 「最近 N 天」含今天（今天是未過完的一天，數字會隨當日累積變動——這是刻意的，
            // 「最近 7 天銷售」排除今天反而不符直覺）。
            RelativePeriod.Last7Days      => new DateRange(today.AddDays(-6),  today),
            RelativePeriod.Last30Days     => new DateRange(today.AddDays(-29), today),
            RelativePeriod.Last90Days     => new DateRange(today.AddDays(-89), today),

            RelativePeriod.MonthToDate    => new DateRange(FirstOfMonth(today),   today),
            RelativePeriod.QuarterToDate  => new DateRange(QuarterOf(today).From, today),
            RelativePeriod.YearToDate     => new DateRange(new DateOnly(today.Year, 1, 1), today),

            RelativePeriod.ThisFiscalPeriod => await FiscalAsync(today, 0, ct),
            RelativePeriod.LastFiscalPeriod => await FiscalAsync(today, 1, ct),

            _ => throw new ArgumentOutOfRangeException(
                nameof(period), period, PzText.Get("Rps_UnsupportedRelativePeriod")),
        };
    }

    public async Task<DateRange> ResolveAsync(
        ReportRequestBase request, string? timeZoneId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Period.HasValue && request.AbsolutePeriod.HasValue)
            throw new InvalidOperationException(
                PzText.Get("Rps_PeriodBothSpecified"));

        // 顯式參數 → 請求自己帶的 → null（由 DateTimeService 退回 IUserContext）。
        // 退到請求這一層是刻意的：要求八支工具每一支都記得把 request.TimeZoneId 傳下來，
        // 等於把正確性押在「下一個寫工具的人會記得」上——那正是 GetExportRowsAsync
        // 當初要離開的那種設計。少寫一個參數不會有任何徵兆，只是日期差一天。
        if (request.Period.HasValue)
            return await ResolveAsync(request.Period.Value, timeZoneId ?? request.TimeZoneId, ct);

        if (request.AbsolutePeriod.HasValue)
        {
            var abs = request.AbsolutePeriod.Value;
            if (abs.To < abs.From)
                throw new InvalidOperationException(PzText.Get("Rps_PeriodEndBeforeStartFmt", abs));
            return abs;
        }

        throw new InvalidOperationException(
            PzText.Get("Rps_PeriodRequired"));
    }

    // ── 日曆算術 ────────────────────────────────────────────────

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static DateRange MonthOf(DateOnly d)
    {
        var start = FirstOfMonth(d);
        return new DateRange(start, start.AddMonths(1).AddDays(-1));
    }

    private static DateRange WeekOf(DateOnly d)
    {
        var offset = ((int)d.DayOfWeek - (int)WeekStart + 7) % 7;
        var start  = d.AddDays(-offset);
        return new DateRange(start, start.AddDays(6));
    }

    private static DateRange QuarterOf(DateOnly d)
    {
        var startMonth = ((d.Month - 1) / 3) * 3 + 1;
        var start      = new DateOnly(d.Year, startMonth, 1);
        return new DateRange(start, start.AddMonths(3).AddDays(-1));
    }

    private static DateRange YearOf(DateOnly d)
        => new(new DateOnly(d.Year, 1, 1), new DateOnly(d.Year, 12, 31));

    // ── 會計期間 ────────────────────────────────────────────────

    /// <summary>
    /// 取「包含今天的會計期間」往前推 <paramref name="back"/> 期。
    ///
    /// 查無資料時**直接擲例外，不退回日曆月**。理由：整個 Phase 0-B 要防的就是
    /// 「數字靜默變成另一個口徑」——排程在凌晨改用日曆月算出一份看起來合理但與帳不符的
    /// 財報，沒有人會發現；而擲例外會讓該次 <c>ReportRun</c> 明確失敗、看得見。
    /// </summary>
    private async Task<DateRange> FiscalAsync(DateOnly today, int back, CancellationToken ct)
    {
        // 這裡刻意**不**繞過廠別過濾器：期間依廠別分開之後，報表要的是本廠的會計行事曆。
        // 繞過的話多廠環境會在「同一期、多筆」之中任取一筆。
        var current = await _db.AccountingPeriods
            .AsNoTracking()
            .Where(p => p.StartDate <= today && today <= p.EndDate)
            .OrderBy(p => p.StartDate)
            .FirstOrDefaultAsync(ct);

        if (current is null)
            throw new InvalidOperationException(
                PzText.Get("Rps_NoPeriodCoveringFmt", today));

        if (back == 0)
            return new DateRange(current.StartDate, current.EndDate);

        var previous = await _db.AccountingPeriods
            .AsNoTracking()
            .Where(p => p.EndDate < current.StartDate)
            .OrderByDescending(p => p.EndDate)
            .Skip(back - 1)
            .FirstOrDefaultAsync(ct);

        if (previous is null)
            throw new InvalidOperationException(
                PzText.Get("Rps_NoPriorPeriodFmt", current.Year, current.Month, back));

        return new DateRange(previous.StartDate, previous.EndDate);
    }
}
