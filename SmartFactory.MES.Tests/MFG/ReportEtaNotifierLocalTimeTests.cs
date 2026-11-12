using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 報工後的 ETA 重算要拿**廠別當地時間**，不是真 UTC。
///
/// ScheduledStart/End 存的是當地時間貼 Utc 標籤；原本報工端傳 DateTime.UtcNow 一路進 EtaRecalculator，
/// 完工時把 ScheduledEnd 寫成早 8 小時，常比開始還早——生產效率分析的排程工時就出現負數（2026-10-27 回報）。
/// </summary>
public class ReportEtaNotifierLocalTimeTests : TestBase
{
    [Theory]
    [InlineData("Asia/Taipei", 8)]
    [InlineData(null, 8)]           // 廠別沒設時區：與全站一致退回 Asia/Taipei
    [InlineData("UTC", 0)]
    public async Task 傳給ETA重算的是廠別當地時間(string? tz, int offsetHours)
    {
        _context.Plants.Add(new Plant { Id = "P001", Name = "P001", DefaultTimeZone = tz });
        _context.SaveChanges();

        var got = new TaskCompletionSource<DateTime>();
        var eta = new Mock<IEtaRecalculator>();
        eta.Setup(e => e.RecalculateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, DateTime, CancellationToken>((_, _, now, _) => got.TrySetResult(now))
           .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<ApplicationDbContext>(_context);
        services.AddSingleton(eta.Object);
        services.AddSingleton(new Mock<SmartFactory.MES.Services.IPushNotificationService>().Object);
        var sp = services.BuildServiceProvider();

        var utcNow = new DateTime(2026, 10, 9, 9, 17, 0, DateTimeKind.Utc);
        new ReportEtaNotifier(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportEtaNotifier>.Instance)
            .QueueRecalculate("D1", machineCode: null, plantId: "P001", now: utcNow);

        var passed = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(utcNow.AddHours(offsetHours), passed);
        Assert.Equal(DateTimeKind.Utc, passed.Kind);   // timestamptz 寫回要 Kind=Utc
    }
}
