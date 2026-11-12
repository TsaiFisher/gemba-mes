using SmartFactory.MES.Modules.RPS.Contracts;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// <see cref="ReportDateDefaults"/>：篩選型報表查詢列的日期預設值。
///
/// <para>這裡守的是兩個「沒有錯誤訊息的失敗」——留白起日被解析成 0001-01-01（每次查詢
/// 掃遍全部歷史），以及使用者明確給的期間被預設值蓋掉。後者更糟：畫面上的日期欄看起來
/// 就是他填的那組，數字卻是別的區間算出來的。</para>
/// </summary>
public class ReportDateDefaultsTests
{
    private static readonly DateOnly Today = new(2026, 9, 21);

    [Fact]
    public void 兩邊都留白_取近三十天含今天()
    {
        var (from, to) = ReportDateDefaults.Apply(null, null, Today);

        Assert.Equal("2026-08-23", from);   // 含今天共 30 天
        Assert.Equal("2026-09-21", to);
    }

    [Fact]
    public void 使用者給的期間一律優先_不被預設值蓋掉()
    {
        var (from, to) = ReportDateDefaults.Apply("2026-01-01", "2026-03-31", Today);

        Assert.Equal("2026-01-01", from);
        Assert.Equal("2026-03-31", to);
    }

    [Fact]
    public void 只給起日_迄日退回今天()
    {
        var (from, to) = ReportDateDefaults.Apply("2026-01-01", null, Today);

        Assert.Equal("2026-01-01", from);
        Assert.Equal("2026-09-21", to);
    }

    [Fact]
    public void 只給迄日_起日以該迄日往前推而不是以今天往前推()
    {
        // 往前推的基準是迄日不是今天，否則「查 1 月底為止」會得到一個起日晚於迄日的空區間。
        var (from, to) = ReportDateDefaults.Apply(null, "2026-01-31", Today);

        Assert.Equal("2026-01-02", from);
        Assert.Equal("2026-01-31", to);
    }

    [Fact]
    public void 解析不掉的字串視同未填_而不是悄悄變成最小日期()
    {
        // 手改網址或舊書籤會送來這種值。原本的 DateOnly.TryParse 會留下 default(DateOnly)，
        // 於是查詢範圍從 0001-01-01 起跳，而畫面上完全看不出來。
        var (from, to) = ReportDateDefaults.Apply("not-a-date", "also-not", Today);

        Assert.Equal("2026-08-23", from);
        Assert.Equal("2026-09-21", to);
    }

    [Fact]
    public void 天數可調整()
    {
        var (from, to) = ReportDateDefaults.Apply(null, null, Today, days: 7);

        Assert.Equal("2026-09-15", from);
        Assert.Equal("2026-09-21", to);
    }

    [Fact]
    public void 基準日接近最小值時不擲例外()
    {
        // 正式環境走不到，但測試裡沒設定的 IDateTimeService 替身回的就是 default(DateOnly)。
        // 若這裡擲 ArgumentOutOfRangeException，失敗訊息會指向一個與受測行為無關的地方。
        var (from, to) = ReportDateDefaults.Apply(null, null, DateOnly.MinValue);

        Assert.Equal("0001-01-01", from);
        Assert.Equal("0001-01-01", to);
    }
}
