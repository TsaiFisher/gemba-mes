using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 線上授權確認的三個來源（SECURITY-OPERATIONS.md §18.15.8）：簽章要求 OR 執行期偵測到 VM／容器 OR 系統參數。
/// 系統參數存在資料庫、持有資料庫的人改得動，所以它只能加嚴——VM 強制與簽章要求都不讀它。
/// 寬限：連不上不轉唯讀，確認期限到了才唯讀。
/// </summary>
public class LicenseOnlineCheckTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 10, 0, 0, 0, TimeSpan.Zero);

    private static LicenseStateInputs Offline(DeploymentKind kind, bool optIn = false, DateTimeOffset? leaseNotAfter = null)
        => new(LicenseSupport.PerpetualExpiry, "offline",
            HostCheck: HostCheckResult.Match, HostKind: kind, OnlineCheckOptIn: optIn,
            LeaseNotAfter: leaseNotAfter, LeaseServerTime: leaseNotAfter?.AddDays(-90));

    private static LicenseStatus Eval(LicenseStateInputs i, DateTimeOffset? now = null)
        => LicenseStateEvaluator.Evaluate(i, now ?? T0);

    // ── 生效值 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DeploymentKind.Physical)]
    [InlineData(DeploymentKind.Wsl)]
    [InlineData(DeploymentKind.Unknown)]   // 偵測不到不強制（讀取失敗不能讓正版停擺）
    public void Offline_NotVm_ParamOff_NeedsNoConfirmation(DeploymentKind kind)
    {
        var i = Offline(kind);
        Assert.False(i.LeaseRequired);
        Assert.Equal(LicenseState.Normal, Eval(i).State);
    }

    [Theory]
    [InlineData(DeploymentKind.Vm)]
    [InlineData(DeploymentKind.Container)]
    public void Offline_OnVm_IsForced_EvenWithParamOff(DeploymentKind kind)
    {
        // 主機識別碼在 VM 設定檔可改：比對通過（Match）也不能證明不是複製品
        var i = Offline(kind, optIn: false);
        Assert.True(i.LeaseForcedByHost);
        Assert.True(i.LeaseRequired);
        var s = Eval(i);
        Assert.Equal(LicenseState.ReadOnly, s.State);
        Assert.Contains("虛擬", s.Reason);
        Assert.Null(s.DisabledAt);   // 沒有可推導的起點，不排停用：確認即恢復
    }

    [Fact]
    public void LeaseLicense_RequiresConfirmation_RegardlessOfParam()
    {
        var i = new LicenseStateInputs(T0.AddYears(1), "lease", OnlineCheckOptIn: false);
        Assert.True(i.LeaseRequired);
        Assert.True(Eval(i).IsWriteBlocked);
    }

    [Fact]
    public void Param_DoesNotApplyToNonOfflineModes()
    {
        Assert.False(new LicenseStateInputs(T0.AddYears(1), "tpm", OnlineCheckOptIn: true).LeaseOptedIn);
    }

    // ── 寬限：連不上不唯讀，到期才唯讀 ────────────────────────────────────

    [Fact]
    public void OptIn_NeverConfirmed_OnlyWarns_DoesNotLock()
    {
        var s = Eval(Offline(DeploymentKind.Physical, optIn: true));
        Assert.Equal(LicenseState.Warning, s.State);
        Assert.Contains("尚未完成確認", s.Reason);
    }

    [Fact]
    public void OptIn_Unreachable_WithinValidity_StaysWritable()
    {
        var i = Offline(DeploymentKind.Physical, optIn: true, leaseNotAfter: T0.AddDays(90)) with { LeaseWarning = "無法連線" };
        Assert.False(Eval(i, T0.AddDays(30)).IsWriteBlocked);
    }

    [Fact]
    public void OptIn_ConfirmationExpired_ReadOnly_ThenDisabledAfter30Days()
    {
        var end = T0.AddDays(90);
        var i = Offline(DeploymentKind.Wsl, optIn: true, leaseNotAfter: end);
        Assert.Equal(LicenseState.Warning, Eval(i, end.AddDays(-1)).State);
        var ro = Eval(i, end);
        Assert.Equal(LicenseState.ReadOnly, ro.State);
        Assert.Equal(end, ro.ReadOnlySince);
        Assert.Equal(LicenseState.Disabled, Eval(i, end.AddDays(30)).State);
    }

    [Fact]
    public void ParamOff_AfterExpiry_Physical_Recovers_ButVmStaysLocked()
    {
        var end = T0.AddDays(90);
        Assert.False(Eval(Offline(DeploymentKind.Physical, optIn: false, leaseNotAfter: end), end.AddDays(1)).IsWriteBlocked);
        Assert.True(Eval(Offline(DeploymentKind.Vm, optIn: false, leaseNotAfter: end), end.AddDays(1)).IsWriteBlocked);
    }

    // ── 狀態服務 ───────────────────────────────────────────────────────────

    [Fact]
    public void StateService_KeepsOptIn_AcrossWatchdogRounds_AndTakesHostKindFromLicense()
    {
        var svc = new LicenseStateService(new ManualTime(T0));
        var ctx = new LicenseContext { LicenseId = "L1", ExpiresAt = LicenseSupport.PerpetualExpiry, BindingMode = "offline",
                                       HostCheck = HostCheckResult.Match, HostKind = DeploymentKind.Physical };
        svc.SetLicense(ctx);
        svc.SetOnlineCheckOptIn(true);
        svc.SetLicense(ctx);   // 看門狗下一輪
        Assert.True(svc.Inputs!.OnlineCheckOptIn);
        Assert.True(svc.LeaseRequired);

        svc.SetLicense(new LicenseContext { LicenseId = "L1", ExpiresAt = LicenseSupport.PerpetualExpiry, BindingMode = "offline",
                                            HostCheck = HostCheckResult.Match, HostKind = DeploymentKind.Vm });
        svc.SetOnlineCheckOptIn(false);
        Assert.True(svc.LeaseRequired);   // 資料庫改成停用，VM 仍要確認
    }

    // ── 變更規則 ───────────────────────────────────────────────────────────

    [Fact]
    public void Change_LeaseLicense_NotApplicable()
        => Assert.Equal("Msg_LicenseOnlineCheckNotOffline",
            LicenseOnlineCheck.CheckChange(new LicenseStateInputs(T0.AddYears(1), "lease"), true, T0));

    [Fact]
    public void Change_DisableOnVm_Refused()
        => Assert.Equal("Msg_LicenseOnlineCheckForcedByHost",
            LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Vm, leaseNotAfter: T0.AddDays(90)), false, T0));

    [Fact]
    public void Change_EnableRequiresValidConfirmation()
    {
        Assert.Equal("Msg_LicenseOnlineCheckConfirmFirst",
            LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Physical), true, T0));
        Assert.Equal("Msg_LicenseOnlineCheckConfirmFirst",
            LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Physical, leaseNotAfter: T0), true, T0));   // 剛好到期
        Assert.Equal("Msg_LicenseOnlineCheckConfirmFirst",
            LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Physical, leaseNotAfter: T0.AddDays(90))
                with { LeaseNoticeAt = T0, LeaseNoticeStatus = "revoked" }, true, T0));
        Assert.Null(LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Physical, leaseNotAfter: T0.AddDays(90)), true, T0));
    }

    [Fact]
    public void Change_DisableOnPhysical_Allowed()
        => Assert.Null(LicenseOnlineCheck.CheckChange(Offline(DeploymentKind.Physical, optIn: true), false, T0));

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData(null, false)]
    public void ParamValue(string? value, bool expected)
        => Assert.Equal(expected, LicenseOnlineCheck.IsEnabledValue(value));
}
