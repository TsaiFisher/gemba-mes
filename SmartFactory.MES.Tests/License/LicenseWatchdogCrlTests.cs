using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 看門狗把撤銷判定接到 <c>/health</c> 的那一段接線。
///
/// <para>
/// 這是階段 1 唯一真正產生營運效果的地方：判定再正確，沒有轉成
/// <c>LicenseWatchdogService.IsHealthy</c> 就沒有任何人會知道。
/// </para>
///
/// <para>
/// <c>IsHealthy</c> / <c>LastError</c> 是 <b>static</b>，而 xUnit 預設平行執行不同測試類別 ——
/// 本類別因此獨占一個 collection，否則會出現間歇性假紅（§14.9 踩過同一個坑）。
/// </para>
/// </summary>
[Collection(LicenseWatchdogCollection.Name)]
public class LicenseWatchdogCrlTests
{
    private static LicenseWatchdogService Build(CrlEvaluation crlResult, ILicenseStateService? state = null)
    {
        var validator = new Mock<ILicenseValidator>();
        validator.Setup(v => v.Validate()).Returns(new LicenseContext
        {
            LicenseId = "LIC-TEST",
            ClientCode = "TEST",
            ClientName = "測試",
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1)
        });

        var crl = new Mock<ILicenseCrlService>();
        crl.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(crlResult);

        var services = new ServiceCollection();
        services.AddSingleton(validator.Object);
        services.AddSingleton(crl.Object);
        if (state is not null) services.AddSingleton(state);
        var provider = services.BuildServiceProvider();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");

        return new LicenseWatchdogService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LicenseWatchdogService>.Instance,
            env.Object);
    }

    [Fact]
    public async Task Revoked_FlipsHealthToUnhealthy()
    {
        var watchdog = Build(new CrlEvaluation(CrlOutcome.Revoked, "授權 LIC-TEST 已被撤銷"));

        await watchdog.ValidateAsync();

        Assert.False(LicenseWatchdogService.IsHealthy);
        Assert.Contains("撤銷", LicenseWatchdogService.LastError ?? "");
    }

    [Fact]
    public async Task SignedRevocation_TurnsSystemReadOnly()
    {
        // §18.8：已驗簽的撤銷提前轉唯讀，不等授權到期
        var state = new LicenseStateService(TimeProvider.System);
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var watchdog = Build(
            new CrlEvaluation(CrlOutcome.Revoked, "授權 LIC-TEST 已被撤銷", revokedAt, "terminated"), state);

        await watchdog.ValidateAsync();

        Assert.Equal(LicenseState.ReadOnly, state.Current.State);
        Assert.Equal(revokedAt, state.Current.ReadOnlySince);
    }

    [Fact]
    public async Task GraceExpired_DoesNotTurnReadOnly()
    {
        // 單向加嚴：取不到清單只是「沒有資訊」，不能當成撤銷
        var state = new LicenseStateService(TimeProvider.System);
        var watchdog = Build(new CrlEvaluation(CrlOutcome.GraceExpired, "寬限已用盡"), state);

        await watchdog.ValidateAsync();

        Assert.False(state.Current.IsWriteBlocked);
    }

    [Fact]
    public async Task GraceExpired_FlipsHealthToUnhealthy()
    {
        var watchdog = Build(new CrlEvaluation(CrlOutcome.GraceExpired, "寬限已用盡"));

        await watchdog.ValidateAsync();

        Assert.False(LicenseWatchdogService.IsHealthy);
    }

    [Fact]
    public async Task Stale_KeepsRunning()
    {
        // 產線系統不因外部網路故障停擺 —— 寬限窗內只留 Warning。
        var watchdog = Build(new CrlEvaluation(CrlOutcome.Stale, "已 3 天未取得清單"));

        await watchdog.ValidateAsync();

        Assert.True(LicenseWatchdogService.IsHealthy);
        Assert.Null(LicenseWatchdogService.LastError);
    }

    [Fact]
    public async Task Ok_IsHealthy()
    {
        var watchdog = Build(new CrlEvaluation(CrlOutcome.Ok, "授權未被撤銷"));

        await watchdog.ValidateAsync();

        Assert.True(LicenseWatchdogService.IsHealthy);
    }

    [Fact]
    public async Task Disabled_IsHealthy_AndDoesNotConsultCrl()
    {
        // 未啟用時（離線廠區）行為必須與階段 1 之前完全相同。
        var watchdog = Build(new CrlEvaluation(CrlOutcome.Disabled, "未啟用撤銷清單檢查"));

        await watchdog.ValidateAsync();

        Assert.True(LicenseWatchdogService.IsHealthy);
    }
}

/// <summary>
/// 讓所有會動到 <c>LicenseWatchdogService</c> 靜態狀態的測試類別共用一個序列化 collection。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LicenseWatchdogCollection
{
    public const string Name = "LicenseWatchdogStatics";
}
