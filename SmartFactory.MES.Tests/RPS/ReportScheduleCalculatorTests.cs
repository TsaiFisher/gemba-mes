using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 排程時刻計算（§7.2 定案 #13）。
///
/// <para>這裡驗的兩個邊界條件都屬於「不處理就會**靜默少一次報表**」：
/// Monthly 的 31 號在 2 月，以及「嚴格晚於」而不是「大於等於」。
/// 兩者都不會拋例外，只會讓某個月的報表沒有出現。</para>
///
/// <para>時區固定用 <c>Asia/Taipei</c>（UTC+8，無夏令時間），
/// 所以當地 09:00 一律等於 UTC 01:00，斷言才讀得懂。</para>
/// </summary>
public class ReportScheduleCalculatorTests
{
    private const string Taipei = "Asia/Taipei";

    private static ReportDefinition Daily(int hour, int minute = 0) => new()
    {
        ScheduleKind = ReportScheduleKind.Daily,
        ScheduleHour = hour,
        ScheduleMinute = minute,
    };

    private static DateTime Utc(int y, int m, int d, int h, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void 非排程的定義回null()
    {
        // 純常用查詢（ScheduleKind = null）永遠不該被派工器撈到。
        var definition = new ReportDefinition();

        Assert.Null(ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 8, 12, 0), Taipei));
    }

    [Fact]
    public void 每日_當天尚未到時取當天()
    {
        // 台北 08:00（UTC 00:00）時算每日 09:00 → 當天台北 09:00 ＝ UTC 01:00
        var next = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 0), Taipei);

        Assert.Equal(Utc(2026, 8, 12, 1), next);
    }

    [Fact]
    public void 每日_當天已過時取隔天()
    {
        // 台北 10:00（UTC 02:00）→ 隔天台北 09:00 ＝ UTC 8/13 01:00
        var next = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 2), Taipei);

        Assert.Equal(Utc(2026, 8, 13, 1), next);
    }

    [Fact]
    public void 每日_剛好等於執行時刻時取隔天()
    {
        // 「嚴格晚於」是刻意的：用大於等於的話，剛執行完以同一時刻重算會得到同一個
        // NextRunAt，於是同一份報表在同一分鐘內被反覆執行。
        var next = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 1), Taipei);

        Assert.Equal(Utc(2026, 8, 13, 1), next);
    }

    [Theory]
    [InlineData(1, 8, 17)]   // 週一：8/12 是週三 → 下週一 8/17
    [InlineData(3, 8, 19)]   // 週三：今天就是週三但 09:00 已過 → 下週三 8/19
    [InlineData(5, 8, 14)]   // 週五：本週五 8/14
    public void 每週_取下一個指定星期幾(int dayOfWeek, int expectedMonth, int expectedDay)
    {
        var definition = new ReportDefinition
        {
            ScheduleKind = ReportScheduleKind.Weekly,
            ScheduleHour = 9,
            ScheduleDayOfWeek = dayOfWeek,
        };

        // 台北 2026-08-12（週三）10:00 ＝ UTC 02:00
        var next = ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 8, 12, 2), Taipei);

        Assert.Equal(Utc(2026, expectedMonth, expectedDay, 1), next);
    }

    [Fact]
    public void 每月_31號在二月取當月最後一日而不是跳過該月()
    {
        // ⭐ 規格明列的邊界條件。跳過的話二月份靜默沒有報表，而且沒有任何錯誤訊息。
        var definition = new ReportDefinition
        {
            ScheduleKind = ReportScheduleKind.Monthly,
            ScheduleHour = 9,
            ScheduleDayOfMonth = 31,
        };

        // 台北 2026-02-01 10:00 → 2026 年二月只有 28 天 → 2/28 台北 09:00 ＝ UTC 2/28 01:00
        var next = ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 2, 1, 2), Taipei);

        Assert.Equal(Utc(2026, 2, 28, 1), next);
    }

    [Fact]
    public void 每月_當月已過時滾到下個月且仍套用最後一日規則()
    {
        var definition = new ReportDefinition
        {
            ScheduleKind = ReportScheduleKind.Monthly,
            ScheduleHour = 9,
            ScheduleDayOfMonth = 31,
        };

        // 台北 2026-01-31 10:00（已過當天 09:00）→ 下個月是 2 月 → 2/28
        var next = ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 1, 31, 2), Taipei);

        Assert.Equal(Utc(2026, 2, 28, 1), next);
    }

    [Fact]
    public void 每月_一般月份取指定日()
    {
        var definition = new ReportDefinition
        {
            ScheduleKind = ReportScheduleKind.Monthly,
            ScheduleHour = 9,
            ScheduleDayOfMonth = 15,
        };

        var next = ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 8, 12, 2), Taipei);

        Assert.Equal(Utc(2026, 8, 15, 1), next);
    }

    [Fact]
    public void 時區不同會算出不同的UTC時刻()
    {
        // 「一律以 UTC 落庫、計算時才換算時區」的意義就在這裡：
        // 同一組設定在不同時區的廠別，UTC 時刻本來就該不同。
        var taipei = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 0), Taipei);
        var utc    = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 0), "UTC");

        Assert.NotEqual(taipei, utc);
        Assert.Equal(Utc(2026, 8, 12, 9), utc);
    }

    [Fact]
    public void 時區代碼無效時退回預設而不是拋例外()
    {
        // 壞掉的時區代碼是一筆資料問題。在背景服務裡拋例外只會變成一行日誌，
        // 而該筆排程從此不再前進——退回預設值至少還會執行。
        var next = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), Utc(2026, 8, 12, 0), "Not/AZone");

        Assert.Equal(Utc(2026, 8, 12, 1), next);   // 等同 Asia/Taipei
    }

    [Fact]
    public void 未指定Kind的時刻欄位以零視之()
    {
        var definition = new ReportDefinition { ScheduleKind = ReportScheduleKind.Daily };

        // 台北 08:00 → 當天 00:00 已過 → 隔天台北 00:00 ＝ UTC 8/12 16:00
        var next = ReportScheduleCalculator.ComputeNextRunUtc(definition, Utc(2026, 8, 12, 0), Taipei);

        Assert.Equal(Utc(2026, 8, 12, 16), next);
    }

    [Fact]
    public void 傳入非UTC的Kind不得拋例外()
    {
        // 從 DB 讀回的 DateTime 常常是 Unspecified。不校正的話 ConvertTimeFromUtc 會拋，
        // 而那個例外在背景迴圈裡只會被記成一行日誌，排程靜默停擺。
        var unspecified = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Unspecified);

        var next = ReportScheduleCalculator.ComputeNextRunUtc(Daily(9), unspecified, Taipei);

        Assert.Equal(Utc(2026, 8, 12, 1), next);
    }
}
