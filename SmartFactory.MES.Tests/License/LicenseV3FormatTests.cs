using System.Security;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權檔 v3 格式（SECURITY-OPERATIONS.md §18.5）：格式版本、欄位組合、綁定方式分流、簽章 payload。
///
/// <para><b>payload 為什麼要釘字面：</b>同一份 payload 在 LicenseValidator、LicenseAuthority、
/// SecurityAnchor 三處各有一份實作（後兩者不在本 repo），任何一處欄位順序或排序規則不同，
/// 症狀只有「簽章無效」。這裡釘住的字面就是另外兩處必須產生的字串。</para>
/// </summary>
public class LicenseV3FormatTests
{
    private const string Fp1 = "AAAA000000000000000000000000000000000000000000000000000000000001";
    private const string Fp2 = "BBBB000000000000000000000000000000000000000000000000000000000002";
    private const string H1 = "CCCC000000000000000000000000000000000000000000000000000000000003";
    private const string H2 = "DDDD000000000000000000000000000000000000000000000000000000000004";

    private static LicenseFile Make(
        string? bindingMode = LicenseBindingModes.Offline,
        string[]? fingerprints = null,
        int seats = 1,
        int formatVersion = LicenseValidator.CurrentFormatVersion,
        string[]? hostIds = null,
        string clientCode = "ACME") =>
        new(
            LicenseId: "LIC-TEST-1",
            ClientCode: clientCode,
            ClientName: "Acme",
            IssuedAt: new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt: new DateTimeOffset(2027, 10, 7, 0, 0, 0, TimeSpan.Zero),
            AllowedFingerprints: fingerprints ?? [Fp1],
            Signature: "",
            FingerprintAlgo: "v2",
            AllowPlainConnection: true,
            FormatVersion: formatVersion,
            BindingMode: bindingMode,
            Seats: seats,
            HostIds: hostIds ?? (bindingMode == LicenseBindingModes.Offline ? [H1] : null));

    // ── 格式版本 ──────────────────────────────────────────────────────────

    [Fact]
    public void FormatVersion3_IsAccepted()
        => LicenseValidator.EnsureFormatVersion(3);

    [Theory]
    [InlineData(0, "v2 或更早")]   // v2 授權沒有這個欄位
    [InlineData(2, "v2")]
    [InlineData(4, "v4")]
    public void OtherFormatVersions_AreRejected_WithReissueHint(int version, string shown)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LicenseValidator.EnsureFormatVersion(version));
        Assert.Contains(shown, ex.Message);
        Assert.Contains("重新核發", ex.Message);
    }

    // ── 欄位組合 ──────────────────────────────────────────────────────────

    [Fact]
    public void Offline_WithFingerprints_IsValid()
        => LicenseValidator.EnsureStructure(Make());

    [Theory]
    [InlineData(LicenseBindingModes.Lease)]
    [InlineData(LicenseBindingModes.Tpm)]
    [InlineData(LicenseBindingModes.Ci)]
    public void NonOffline_WithoutFingerprints_IsValid(string mode)
        => LicenseValidator.EnsureStructure(Make(mode, []));

    [Fact]
    public void Offline_WithoutFingerprints_IsRejected()
        => Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(fingerprints: [])));

    [Theory]
    [InlineData(LicenseBindingModes.Lease)]
    [InlineData(LicenseBindingModes.Tpm)]
    [InlineData(LicenseBindingModes.Ci)]
    public void NonOffline_WithFingerprints_IsRejected(string mode)
    {
        // 租約授權帶指紋會變成「兩套規則哪個算數」——驗證端不接受
        var ex = Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(mode, [Fp1])));
        Assert.Contains(mode, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OFFLINE")]     // 大小寫敏感：簽章內就是小寫，不做寬鬆比對
    [InlineData("wildcard")]
    public void UnknownBindingMode_IsRejected(string? mode)
        => Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(mode)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Seats_BelowOne_AreRejected(int seats)
        => Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(seats: seats)));

    // ── 綁定方式分流 ──────────────────────────────────────────────────────

    [Fact]
    public void Offline_IsSupported()
        => LicenseValidator.EnsureBindingModeSupported(LicenseBindingModes.Offline);

    [Fact]
    public void Lease_IsSupported_WritesGovernedByLeaseState()
        => LicenseValidator.EnsureBindingModeSupported(LicenseBindingModes.Lease);

    [Theory]
    [InlineData(LicenseBindingModes.Tpm)]
    [InlineData(LicenseBindingModes.Ci)]
    public void TpmCi_AreNotYetSupported_AndNeverFallBackToFingerprints(string mode)
    {
        // §18.5：offline 以外沒有租約或綁定憑證一律不放行
        var ex = Assert.Throws<SecurityException>(() => LicenseValidator.EnsureBindingModeSupported(mode));
        Assert.Contains(mode, ex.Message);
    }

    // ── 簽章 payload ──────────────────────────────────────────────────────

    [Fact]
    public void SignaturePayload_HasPinnedFieldOrder()
    {
        var payload = LicenseValidator.BuildSignaturePayload(Make(fingerprints: [Fp2, Fp1], seats: 2, hostIds: [H2, H1]));

        Assert.Equal(
            "{\"formatVersion\":3,\"licenseId\":\"LIC-TEST-1\",\"clientCode\":\"ACME\",\"clientName\":\"Acme\"," +
            "\"issuedAt\":\"2026-10-08T00:00:00+00:00\",\"expiresAt\":\"2027-10-07T00:00:00+00:00\"," +
            "\"bindingMode\":\"offline\",\"seats\":2," +
            $"\"allowedFingerprints\":[\"{Fp1}\",\"{Fp2}\"]," +
            $"\"hostIds\":[\"{H1}\",\"{H2}\"]," +
            "\"fingerprintAlgo\":\"v2\",\"allowPlainConnection\":true}",
            payload);
    }

    [Fact]
    public void SignaturePayload_CoversV3Fields()
    {
        // 改綁定方式、座數或格式版本都必須讓簽章失效
        var baseline = LicenseValidator.BuildSignaturePayload(Make());

        Assert.NotEqual(baseline, LicenseValidator.BuildSignaturePayload(Make(seats: 5)));
        Assert.NotEqual(baseline, LicenseValidator.BuildSignaturePayload(Make(LicenseBindingModes.Lease)));
        Assert.NotEqual(baseline, LicenseValidator.BuildSignaturePayload(Make(formatVersion: 4)));
        Assert.NotEqual(baseline, LicenseValidator.BuildSignaturePayload(Make(hostIds: [H2])));   // 換主機必須讓簽章失效
    }

    // ── 主機識別碼（§18.15.5）────────────────────────────────────────────

    [Fact]
    public void Offline_WithoutHostIds_IsRejected_ForCustomers()
    {
        var ex = Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(hostIds: [])));
        Assert.Contains("hostIds", ex.Message);
    }

    [Fact]
    public void Offline_WithoutHostIds_IsAllowed_ForInternal()   // CI runner 每次都是不同機器
        => LicenseValidator.EnsureStructure(Make(hostIds: [], clientCode: LicenseValidator.InternalClientCode));

    [Fact]
    public void Lease_WithHostIds_IsRejected()
        => Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(LicenseBindingModes.Lease, [], hostIds: [H1])));

    [Theory]
    [InlineData("not-hex")]
    [InlineData("CCCC")]
    public void HostIds_MustBe64Hex(string bad)
        => Assert.Throws<SecurityException>(() => LicenseValidator.EnsureStructure(Make(hostIds: [bad])));

    // ── 主機識別碼閘門（§18.15.9）：不符與讀不到都擋，不再轉唯讀／只提醒 ────────────

    [Fact]
    public void HostGate_NoHostIds_DoesNotEvenCallDetector()
    {
        var called = false;
        var host = LicenseHostGate.Enforce(null, () => { called = true; return new(H1, DeploymentKind.Wsl, "x", null); });
        Assert.Null(host);
        Assert.False(called);   // WSL 上偵測會啟動 reg.exe，用不到就不要跑
    }

    [Fact]
    public void HostGate_Match_ReturnsDetectedHost()
    {
        var host = LicenseHostGate.Enforce([H2, H1], () => new(H1.ToLowerInvariant(), DeploymentKind.Vm, "x", null));
        Assert.Equal(DeploymentKind.Vm, host!.Kind);   // VM 強制線上確認要靠它
    }

    [Fact]
    public void HostGate_Mismatch_Blocks()
    {
        var ex = Assert.Throws<SecurityException>(() =>
            LicenseHostGate.Enforce([H1], () => new(H2, DeploymentKind.Physical, "x", null)));
        Assert.Equal(LicenseHostGate.Mismatch, LicenseHostGate.ReasonOf(ex));
        Assert.Contains("不連線資料庫", ex.Message);
    }

    [Fact]
    public void HostGate_Unavailable_AlsoBlocks_AndCarriesTheFixHint()
    {
        // 只提醒的話，關掉 WSL interop（或拿掉 /run 的檔案）就能繞過
        var ex = Assert.Throws<SecurityException>(() =>
            LicenseHostGate.Enforce([H1], () => new(null, DeploymentKind.Wsl, "x", "請重開 WSL")));
        Assert.Equal(LicenseHostGate.Unavailable, LicenseHostGate.ReasonOf(ex));
        Assert.Contains("請重開 WSL", ex.Message);
    }

    [Fact]
    public void HostGate_ReasonOf_SeesThroughReflectionWrapping()
    {
        // Security DLL 的例外經反射呼叫會被包成 TargetInvocationException
        var inner = LicenseHostGate.Blocked(LicenseHostGate.Mismatch, "x");
        var wrapped = new System.Reflection.TargetInvocationException(inner);
        Assert.Equal(LicenseHostGate.Mismatch, LicenseHostGate.ReasonOf(wrapped));
        Assert.Null(LicenseHostGate.ReasonOf(new SecurityException("簽章無效")));
        Assert.Null(LicenseHostGate.ReasonOf(null));
    }

    [Fact]
    public void Perpetual_IsYear9999_AndShownAsPermanent()
    {
        Assert.True(LicenseSupport.IsPerpetual(LicenseSupport.PerpetualExpiry));
        Assert.Equal("永久", LicenseSupport.FormatExpiry(LicenseSupport.PerpetualExpiry));
        Assert.Equal("2027-10-07", LicenseSupport.FormatExpiry(new DateTimeOffset(2027, 10, 7, 0, 0, 0, TimeSpan.Zero)));
    }
}
