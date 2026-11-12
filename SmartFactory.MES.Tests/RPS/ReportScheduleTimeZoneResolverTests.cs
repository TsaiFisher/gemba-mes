using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 排程時區的解析順序（manual-test-plan §11 #10 收口）。
///
/// <para>這一組每一條都屬於「錯了不會有徵兆」：算錯時區不會拋例外、不會少一份報表，
/// 只是**在別的時刻寄出、涵蓋別的一天**，而畫面上兩張排程長得一模一樣。</para>
/// </summary>
public class ReportScheduleTimeZoneResolverTests : TestBase
{
    private ReportDefinition Definition(string? plantId, string ownerId) => new()
    {
        Name = "N", ToolKey = "T", ParamsJson = "{}", OutputFormat = "xlsx",
        OwnerUserId = ownerId, OwnerRoleId = "R", CreatedByRoleId = "R",
        PlantId = plantId,
    };

    private async Task<string> AddUserAsync(string id, string? tz)
    {
        _context.Users.Add(new ApplicationUser
        {
            Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(),
            Email = id + "@x.test", TimeZoneId = tz,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task AddPlantAsync(string id, string? tz)
    {
        _context.Plants.Add(new Plant { Id = id, Name = id, DefaultTimeZone = tz });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task 廠別時區優先於建立者的個人時區()
    {
        // 這是本條規則存在的理由：全站的時區鏈是
        // `ActivePlant.DefaultTimeZone ?? 使用者個人 tz`，排程不該是唯一的例外。
        await AddPlantAsync("P9", "America/New_York");
        await AddUserAsync("u1", "Asia/Tokyo");

        var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(
            _context, Definition("P9", "u1"));

        Assert.Equal("America/New_York", tz);
    }

    [Fact]
    public async Task 廠別沒設時區時退回建立者的個人時區()
    {
        await AddPlantAsync("P8", null);
        await AddUserAsync("u2", "Asia/Tokyo");

        var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(
            _context, Definition("P8", "u2"));

        Assert.Equal("Asia/Tokyo", tz);
    }

    [Fact]
    public async Task 兩者都沒有時回null_交給計算器自己的fallback()
    {
        // 刻意不在這裡回 "Asia/Taipei"：fallback 只能有一份，
        // 兩個地方各寫一次的話，改了其中一個就會出現「存檔與派工算出不同時刻」。
        await AddPlantAsync("P7", null);
        await AddUserAsync("u3", null);

        var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(
            _context, Definition("P7", "u3"));

        Assert.Null(tz);
    }

    [Fact]
    public async Task 排程沒有廠別時直接看建立者()
    {
        await AddUserAsync("u4", "Europe/London");

        var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(
            _context, Definition(null, "u4"));

        Assert.Equal("Europe/London", tz);
    }

    [Fact]
    public async Task 廠別代碼查無此廠時不拋例外_退回建立者()
    {
        // 廠別被刪、或資料是從別的環境搬來的。背景派工器裡拋例外只會變成一行日誌，
        // 而那一份排程從此不再往前推進——沒有任何人會發現。
        await AddUserAsync("u5", "Asia/Seoul");

        var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(
            _context, Definition("NO_SUCH_PLANT", "u5"));

        Assert.Equal("Asia/Seoul", tz);
    }

    [Fact]
    public async Task 同一份排程換人建立_只要同廠就算出同一個時刻()
    {
        // §11 #10 的症狀本體：原本讀建立者個人時區，於是「每日 08:00」換個人建立
        // 就在不同時刻寄出，而畫面上看不出任何差別。
        await AddPlantAsync("P6", "America/New_York");
        await AddUserAsync("owner-a", "Asia/Tokyo");
        await AddUserAsync("owner-b", null);

        var a = await ReportScheduleTimeZoneResolver.ResolveAsync(_context, Definition("P6", "owner-a"));
        var b = await ReportScheduleTimeZoneResolver.ResolveAsync(_context, Definition("P6", "owner-b"));

        Assert.Equal(a, b);

        var daily = new ReportDefinition
        {
            Name = "N", ToolKey = "T", ParamsJson = "{}", OutputFormat = "xlsx",
            OwnerUserId = "owner-a", OwnerRoleId = "R", CreatedByRoleId = "R",
            ScheduleKind = ReportScheduleKind.Daily, ScheduleHour = 8, ScheduleMinute = 0,
        };
        var afterUtc = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            ReportScheduleCalculator.ComputeNextRunUtc(daily, afterUtc, a),
            ReportScheduleCalculator.ComputeNextRunUtc(daily, afterUtc, b));
    }
}
