using SmartFactory.MES.Tests.Shared;
using Moq;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// §5.2.1 相對期間解算。
///
/// 這是 Phase 0-B 的地基：常用查詢與排程都靠它才有「滾動視窗」語意
/// （存絕對日期的「上月銷售」下個月點開還是上個月的資料）。
/// 解錯一天不會有任何錯誤訊息，只會讓報表少算或多算一天的單，所以每個邊界都要有斷言。
/// </summary>
public class RelativePeriodResolverTests : TestBase
{
    // 期間自 2026-08-30 起是 IPlantEntity（每廠一份），種子與查詢都要有廠別，
    // 否則複合主鍵的 PlantId 為 null，EF 直接拒絕追蹤。
    private const string PlantId = "P001";

    private readonly Mock<IDateTimeService> _dt = new();

    /// <summary>2026-05-14 是星期四，落在 Q2、當月 31 天——刻意選能同時試出週/月/季邊界的一天。</summary>
    private static readonly DateOnly Today = new(2026, 5, 14);

    public RelativePeriodResolverTests()
    {
        _context.CurrentPlantId = PlantId;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    private RelativePeriodResolver Resolver() => new(_dt.Object, _context);

    private static DateRange R(int fromY, int fromM, int fromD, int toY, int toM, int toD)
        => new(new DateOnly(fromY, fromM, fromD), new DateOnly(toY, toM, toD));

    // ── 日／週 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(RelativePeriod.Today,     2026, 5, 14, 2026, 5, 14)]
    [InlineData(RelativePeriod.Yesterday, 2026, 5, 13, 2026, 5, 13)]
    // 2026-05-14 是星期四 → 本週一 = 5/11、本週日 = 5/17
    [InlineData(RelativePeriod.ThisWeek,  2026, 5, 11, 2026, 5, 17)]
    [InlineData(RelativePeriod.LastWeek,  2026, 5,  4, 2026, 5, 10)]
    public async Task 日與週(RelativePeriod p, int fy, int fm, int fd, int ty, int tm, int td)
        => Assert.Equal(R(fy, fm, fd, ty, tm, td), await Resolver().ResolveAsync(p));

    /// <summary>
    /// 週起始固定星期一，**不隨文化設定改變**。若哪天有人改成讀
    /// <c>CultureInfo.CurrentCulture.FirstDayOfWeek</c>，同一個「本週」會在畫面（zh-TW 週日起）
    /// 與排程（行程預設語系）解出不同區間——那正是 §5.5 T7 要防的漂移。
    /// </summary>
    [Fact]
    public async Task 週起始固定星期一_不受文化設定影響()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // zh-TW 的 FirstDayOfWeek 是星期日；若實作讀文化設定，這裡會解成 5/10~5/16
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("zh-TW");
            Assert.Equal(R(2026, 5, 11, 2026, 5, 17),
                await Resolver().ResolveAsync(RelativePeriod.ThisWeek));

            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal(R(2026, 5, 11, 2026, 5, 17),
                await Resolver().ResolveAsync(RelativePeriod.ThisWeek));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>週一當天的「本週」是它自己那一週，不是上一週（offset 取模的邊界）。</summary>
    [Fact]
    public async Task 週一當天的本週從當天起算()
    {
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 11));

        Assert.Equal(R(2026, 5, 11, 2026, 5, 17), await Resolver().ResolveAsync(RelativePeriod.ThisWeek));
        Assert.Equal(R(2026, 5,  4, 2026, 5, 10), await Resolver().ResolveAsync(RelativePeriod.LastWeek));
    }

    /// <summary>週日（DayOfWeek=0）是取模公式最容易算錯的一天：它屬於前一個週一起算的那一週。</summary>
    [Fact]
    public async Task 週日屬於前一個週一起算的那一週()
    {
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 17));

        Assert.Equal(R(2026, 5, 11, 2026, 5, 17), await Resolver().ResolveAsync(RelativePeriod.ThisWeek));
    }

    // ── 月／季／年 ──────────────────────────────────────────────

    [Theory]
    [InlineData(RelativePeriod.ThisMonth,   2026, 5, 1,  2026, 5,  31)]
    [InlineData(RelativePeriod.LastMonth,   2026, 4, 1,  2026, 4,  30)]
    [InlineData(RelativePeriod.ThisQuarter, 2026, 4, 1,  2026, 6,  30)]
    [InlineData(RelativePeriod.LastQuarter, 2026, 1, 1,  2026, 3,  31)]
    [InlineData(RelativePeriod.ThisYear,    2026, 1, 1,  2026, 12, 31)]
    [InlineData(RelativePeriod.LastYear,    2025, 1, 1,  2025, 12, 31)]
    public async Task 月季年(RelativePeriod p, int fy, int fm, int fd, int ty, int tm, int td)
        => Assert.Equal(R(fy, fm, fd, ty, tm, td), await Resolver().ResolveAsync(p));

    /// <summary>1 月的「上月」要跨年到去年 12 月；Q1 的「上季」要跨年到去年 Q4。</summary>
    [Fact]
    public async Task 一月的上月與上季跨年()
    {
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 1, 15));

        Assert.Equal(R(2025, 12, 1, 2025, 12, 31), await Resolver().ResolveAsync(RelativePeriod.LastMonth));
        Assert.Equal(R(2025, 10, 1, 2025, 12, 31), await Resolver().ResolveAsync(RelativePeriod.LastQuarter));
    }

    /// <summary>閏年 2 月要 29 天——月底用 AddMonths(1).AddDays(-1) 而非固定天數的理由。</summary>
    [Fact]
    public async Task 閏年二月月底為二十九日()
    {
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2028, 2, 10));

        Assert.Equal(R(2028, 2, 1, 2028, 2, 29), await Resolver().ResolveAsync(RelativePeriod.ThisMonth));
    }

    [Theory]
    [InlineData(3,  2026, 1, 1, 2026, 3, 31)]   // Q1 最後一天
    [InlineData(4,  2026, 4, 1, 2026, 6, 30)]   // Q2 第一天
    [InlineData(12, 2026, 10, 1, 2026, 12, 31)] // Q4
    public async Task 季邊界(int month, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var day = month == 3 ? 31 : 1;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, month, day));

        Assert.Equal(R(fy, fm, fd, ty, tm, td), await Resolver().ResolveAsync(RelativePeriod.ThisQuarter));
    }

    // ── 滾動 N 天與 ToDate ──────────────────────────────────────

    /// <summary>「最近 N 天」**含今天**，所以是 today−(N−1) 到 today，共 N 天。</summary>
    [Theory]
    [InlineData(RelativePeriod.Last7Days,  2026, 5,  8)]
    [InlineData(RelativePeriod.Last30Days, 2026, 4, 15)]
    [InlineData(RelativePeriod.Last90Days, 2026, 2, 14)]
    public async Task 最近N天含今天(RelativePeriod p, int fy, int fm, int fd)
    {
        var range = await Resolver().ResolveAsync(p);

        Assert.Equal(new DateOnly(fy, fm, fd), range.From);
        Assert.Equal(Today, range.To);
    }

    [Fact]
    public async Task 最近七天恰為七天()
    {
        var range = await Resolver().ResolveAsync(RelativePeriod.Last7Days);

        Assert.Equal(7, range.To.DayNumber - range.From.DayNumber + 1);
    }

    [Theory]
    [InlineData(RelativePeriod.MonthToDate,   2026, 5, 1)]
    [InlineData(RelativePeriod.QuarterToDate, 2026, 4, 1)]
    [InlineData(RelativePeriod.YearToDate,    2026, 1, 1)]
    public async Task ToDate類止於今天(RelativePeriod p, int fy, int fm, int fd)
    {
        var range = await Resolver().ResolveAsync(p);

        Assert.Equal(new DateOnly(fy, fm, fd), range.From);
        Assert.Equal(Today, range.To);
    }

    // ── 會計期間 ────────────────────────────────────────────────

    private void SeedPeriods(params (int Year, int Month)[] periods)
    {
        foreach (var (y, m) in periods)
        {
            var start = new DateOnly(y, m, 1);
            _context.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId  = $"{y}{m:00}",
                PlantId   = PlantId,
                Year      = y,
                Month     = m,
                StartDate = start,
                EndDate   = start.AddMonths(1).AddDays(-1),
            });
        }
        _context.SaveChanges();
    }

    [Fact]
    public async Task 會計期間_本期與上期()
    {
        SeedPeriods((2026, 3), (2026, 4), (2026, 5));

        Assert.Equal(R(2026, 5, 1, 2026, 5, 31),
            await Resolver().ResolveAsync(RelativePeriod.ThisFiscalPeriod));
        Assert.Equal(R(2026, 4, 1, 2026, 4, 30),
            await Resolver().ResolveAsync(RelativePeriod.LastFiscalPeriod));
    }

    /// <summary>
    /// 會計期間**不必等於日曆月**（這正是它存在的理由）。
    /// 這裡的 5 月期間是 4/26–5/25，解出來必須是它而不是 5/1–5/31。
    /// </summary>
    [Fact]
    public async Task 會計期間與日曆月不同時以會計期間為準()
    {
        _context.AccountingPeriods.AddRange(
            new AccountingPeriod
            {
                PeriodId = "202604", PlantId = PlantId, Year = 2026, Month = 4,
                StartDate = new DateOnly(2026, 3, 26), EndDate = new DateOnly(2026, 4, 25),
            },
            new AccountingPeriod
            {
                PeriodId = "202605", PlantId = PlantId, Year = 2026, Month = 5,
                StartDate = new DateOnly(2026, 4, 26), EndDate = new DateOnly(2026, 5, 25),
            });
        _context.SaveChanges();

        Assert.Equal(R(2026, 4, 26, 2026, 5, 25),
            await Resolver().ResolveAsync(RelativePeriod.ThisFiscalPeriod));
        Assert.Equal(R(2026, 3, 26, 2026, 4, 25),
            await Resolver().ResolveAsync(RelativePeriod.LastFiscalPeriod));
    }

    /// <summary>
    /// 查無涵蓋今天的會計期間時**擲例外，不退回日曆月**。
    ///
    /// 這是刻意的：排程在凌晨靜默改用日曆月，會產出一份看起來合理、但與帳務口徑不符的
    /// 財報，沒有人會發現；擲例外則讓該次執行明確失敗、看得見。
    /// </summary>
    [Fact]
    public async Task 查無會計期間時擲例外而非退回日曆月()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver().ResolveAsync(RelativePeriod.ThisFiscalPeriod));

        PzAssert.Message("Rps_NoPeriodCoveringFmt", ex.Message);
    }

    [Fact]
    public async Task 無上一個會計期間時擲例外()
    {
        SeedPeriods((2026, 5));   // 只有本期

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver().ResolveAsync(RelativePeriod.LastFiscalPeriod));
    }

    // ── 請求層解算 ──────────────────────────────────────────────

    private sealed class FakeRequest : ReportRequestBase;

    [Fact]
    public async Task 請求以Period優先解算()
    {
        var req = new FakeRequest { Period = RelativePeriod.LastMonth };

        Assert.Equal(R(2026, 4, 1, 2026, 4, 30), await Resolver().ResolveAsync(req));
    }

    [Fact]
    public async Task 請求可用絕對區間()
    {
        var req = new FakeRequest { AbsolutePeriod = R(2026, 1, 1, 2026, 1, 31) };

        Assert.Equal(R(2026, 1, 1, 2026, 1, 31), await Resolver().ResolveAsync(req));
    }

    /// <summary>兩者同時給＝「這份報表查哪一段」沒有單一答案，必須擋掉而不是選一個。</summary>
    [Fact]
    public async Task 兩種期間同時指定時擲例外()
    {
        var req = new FakeRequest
        {
            Period         = RelativePeriod.LastMonth,
            AbsolutePeriod = R(2026, 1, 1, 2026, 1, 31),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver().ResolveAsync(req));
        Assert.False(req.HasExactlyOnePeriod);
    }

    [Fact]
    public async Task 兩種期間皆未指定時擲例外()
    {
        var req = new FakeRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver().ResolveAsync(req));
        Assert.False(req.HasExactlyOnePeriod);
    }

    [Fact]
    public async Task 絕對區間迄日早於起日時擲例外()
    {
        var req = new FakeRequest { AbsolutePeriod = R(2026, 5, 31, 2026, 5, 1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver().ResolveAsync(req));
    }

    // ── 時區 ────────────────────────────────────────────────────

    /// <summary>
    /// 時區必須一路傳到 <c>IDateTimeService.GetLocalDate</c>。
    /// 排程沒有 HTTP 上下文，不傳就會退回行程預設，跨時區廠別的「昨天」會差一天。
    /// </summary>
    [Fact]
    public async Task 時區參數會傳給DateTimeService()
    {
        await Resolver().ResolveAsync(RelativePeriod.Today, "America/New_York");

        _dt.Verify(d => d.GetLocalDate("America/New_York"), Times.Once);
    }

    // ── 當地日界線 → UTC（timestamptz 欄位用）────────────────────

    /// <summary>
    /// 紐約廠的 2026-10-06 從 UTC 04:00（夏令 EDT）開始；台北的同一天從前一天 UTC 16:00 開始。
    /// 2026-10-07 人工驗收 TZ-3：工具原本用 <c>ToDateTime(TimeOnly.MinValue)</c>，Kind=Unspecified 被 Npgsql 拒寫，
    /// 就算標成 Utc 也是 UTC 午夜、與紐約的「昨天」差 4 小時。
    /// </summary>
    [Theory]
    [InlineData("America/New_York", 2026, 10, 6, 2026, 10, 6, 4)]
    [InlineData("America/New_York", 2026, 12, 1, 2026, 12, 1, 5)]   // 冬令 EST
    [InlineData("Asia/Taipei",      2026, 10, 6, 2026, 10, 5, 16)]
    public void LocalDayStartUtc_換成該時區午夜的UTC時刻(
        string tz, int y, int m, int d, int uy, int um, int ud, int uh)
    {
        var utc = Resolver().LocalDayStartUtc(new DateOnly(y, m, d), tz);

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(uy, um, ud, uh, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void LocalDayStartUtc_未給時區時退回使用者時區且Kind為Utc()
    {
        // DateTimeService.ToUtcTime 依使用者時區換算；替身模擬台北（+8）。
        _dt.Setup(d => d.ToUtcTime(It.IsAny<DateTime>())).Returns<DateTime>(x => x.AddHours(-8));

        var utc = Resolver().LocalDayStartUtc(new DateOnly(2026, 10, 6), null);

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void LocalDayStartUtc_時區代碼解不開時退回台北()
        => Assert.Equal(new DateTime(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc),
                        Resolver().LocalDayStartUtc(new DateOnly(2026, 10, 6), "Not/AZone"));

    // ── DateRange ───────────────────────────────────────────────

    [Fact]
    public void DateRange_Contains_兩端皆含()
    {
        var range = R(2026, 5, 1, 2026, 5, 31);

        Assert.True(range.Contains(new DateOnly(2026, 5, 1)));
        Assert.True(range.Contains(new DateOnly(2026, 5, 31)));
        Assert.False(range.Contains(new DateOnly(2026, 4, 30)));
        Assert.False(range.Contains(new DateOnly(2026, 6, 1)));
    }
}
