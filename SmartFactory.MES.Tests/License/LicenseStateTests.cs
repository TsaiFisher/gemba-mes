using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權狀態判定（SECURITY-OPERATIONS.md §18.8）：正常 → 提醒 → 唯讀 → 停用，終止聲明凌駕一切。
/// </summary>
public class LicenseStateTests
{
    private static readonly DateTimeOffset Expiry = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LicenseStatus At(DateTimeOffset now, string mode = "offline",
        DateTimeOffset? revokedAt = null, bool sunset = false)
        => LicenseStateEvaluator.Evaluate(
            new LicenseStateInputs(Expiry, mode, revokedAt, revokedAt is null ? null : "terminated", sunset), now);

    [Fact]
    public void FarFromExpiry_IsNormal()
        => Assert.Equal(LicenseState.Normal, At(Expiry.AddDays(-60)).State);

    [Theory]
    [InlineData("offline", -29, LicenseState.Warning)]   // 離線：30 天前開始提醒
    [InlineData("offline", -31, LicenseState.Normal)]
    [InlineData("tpm",     -13, LicenseState.Warning)]   // 其餘：14 天（lease 另需租約，見 LeaseClientTests）
    [InlineData("tpm",     -15, LicenseState.Normal)]
    public void WarningThreshold_DependsOnBindingMode(string mode, int days, LicenseState expected)
        => Assert.Equal(expected, At(Expiry.AddDays(days), mode).State);

    [Fact]
    public void AtExpiry_BecomesReadOnly_NotStopped()
    {
        var s = At(Expiry);
        Assert.Equal(LicenseState.ReadOnly, s.State);
        Assert.True(s.IsWriteBlocked);
        Assert.Equal(Expiry, s.ReadOnlySince);
        Assert.Equal(Expiry.AddDays(30), s.DisabledAt);
    }

    [Fact]
    public void ThirtyDaysAfterReadOnly_BecomesDisabled()
    {
        Assert.Equal(LicenseState.ReadOnly, At(Expiry.AddDays(30).AddSeconds(-1)).State);
        Assert.Equal(LicenseState.Disabled, At(Expiry.AddDays(30)).State);
    }

    [Fact]
    public void SignedRevocation_TriggersReadOnlyBeforeExpiry()
    {
        var revoked = Expiry.AddDays(-100);
        var s = At(revoked.AddDays(1), revokedAt: revoked);
        Assert.Equal(LicenseState.ReadOnly, s.State);
        Assert.Equal(revoked, s.ReadOnlySince);
        Assert.Contains("撤銷", s.Reason);
    }

    [Fact]
    public void RevocationDatedInFuture_DoesNotApplyYet()
        => Assert.Equal(LicenseState.Normal, At(Expiry.AddDays(-100), revokedAt: Expiry.AddDays(-50)).State);

    [Fact]
    public void Sunset_OverridesEverything_IncludingDisabled()
    {
        var s = At(Expiry.AddYears(5), revokedAt: Expiry.AddDays(-10), sunset: true);
        Assert.Equal(LicenseState.Unlocked, s.State);
        Assert.False(s.IsWriteBlocked);
    }

    // ── 服務 ───────────────────────────────────────────────────────────────

    private static (LicenseStateService svc, ManualTime time) Service()
    {
        var time = new ManualTime(Expiry.AddDays(-60));
        return (new LicenseStateService(time), time);
    }

    private static LicenseContext Ctx(DateTimeOffset expiresAt) => new()
    {
        LicenseId = "LIC-1", ClientCode = "ACME", ExpiresAt = expiresAt, BindingMode = "offline",
    };

    [Fact]
    public void Service_BeforeAnyLicense_DoesNotBlock()
        => Assert.False(Service().svc.Current.IsWriteBlocked);

    [Fact]
    public void Service_TransitionsAtExpiry_WithoutAnotherWatchdogRound()
    {
        var (svc, time) = Service();
        svc.SetLicense(Ctx(Expiry));
        Assert.Equal(LicenseState.Normal, svc.Current.State);

        time.SetUtcNow(Expiry.AddMinutes(1));
        Assert.Equal(LicenseState.ReadOnly, svc.Current.State);
    }

    [Fact]
    public void Service_RenewedLicense_LeavesReadOnly()
    {
        var (svc, time) = Service();
        svc.SetLicense(Ctx(Expiry));
        time.SetUtcNow(Expiry.AddDays(1));
        Assert.True(svc.Current.IsWriteBlocked);

        svc.SetLicense(Ctx(Expiry.AddYears(1)));   // 換發（匯入新授權檔）
        Assert.False(svc.Current.IsWriteBlocked);
    }

    [Fact]
    public void Service_Revocation_SurvivesLicenseRefresh()
    {
        // 單向加嚴：看門狗下一輪重設授權（或抓不到撤銷清單）都不能讓撤銷消失
        var (svc, time) = Service();
        svc.SetLicense(Ctx(Expiry));
        svc.SetRevoked(time.GetUtcNow().AddDays(-1), "terminated");
        svc.SetLicense(Ctx(Expiry));
        Assert.Equal(LicenseState.ReadOnly, svc.Current.State);
    }

    [Fact]
    public void Service_EarlierRevocationWins()
    {
        var (svc, time) = Service();
        svc.SetLicense(Ctx(Expiry));
        var early = time.GetUtcNow().AddDays(-40);
        svc.SetRevoked(early, "a");
        svc.SetRevoked(time.GetUtcNow().AddDays(-1), "b");
        Assert.Equal(early, svc.Current.ReadOnlySince);
        Assert.Equal(LicenseState.Disabled, svc.Current.State);   // 40 天前撤銷 → 已滿 30 天
    }

    [Fact]
    public void Service_Sunset_WorksWithoutLicense_AndIsPermanent()
    {
        var (svc, _) = Service();
        svc.SetSunset();
        Assert.Equal(LicenseState.Unlocked, svc.Current.State);

        svc.SetLicense(Ctx(Expiry.AddYears(-1)));   // 之後再讀到過期授權也不回頭
        Assert.Equal(LicenseState.Unlocked, svc.Current.State);
    }
    // ── 主機識別碼與永久授權（§18.15）─────────────────────────────────────

    // 不符與讀不到在驗證時就擋下、系統不啟動（LicenseHostGate，§18.15.9），不會進到狀態機

    [Fact]
    public void HostMatch_IsNormal()
        => Assert.Equal(LicenseState.Normal, LicenseStateEvaluator.Evaluate(
            new LicenseStateInputs(Expiry, "offline", HostCheck: HostCheckResult.Match), Expiry.AddDays(-200)).State);

    [Fact]
    public void Perpetual_NeverWarnsOrExpires()
    {
        var s = LicenseStateEvaluator.Evaluate(
            new LicenseStateInputs(LicenseSupport.PerpetualExpiry, "offline", HostCheck: HostCheckResult.Match),
            new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(LicenseState.Normal, s.State);
    }

    [Fact]
    public void Service_CarriesHostResultFromValidatedLicense()
    {
        var svc = new LicenseStateService(TimeProvider.System);
        svc.SetLicense(new LicenseContext { LicenseId = "L", ExpiresAt = LicenseSupport.PerpetualExpiry,
            BindingMode = "offline", HostCheck = HostCheckResult.Match, HostKind = DeploymentKind.Vm });
        Assert.Equal(HostCheckResult.Match, svc.Inputs!.HostCheck);
        Assert.True(svc.LeaseRequired);   // VM 上的 offline 授權強制線上確認
    }
}

/// <summary>可手動撥動的時鐘（測試專案離線還原，沒有 Microsoft.Extensions.TimeProvider.Testing）。</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public void SetUtcNow(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}
