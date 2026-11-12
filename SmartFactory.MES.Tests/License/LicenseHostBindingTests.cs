using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 主機綁定擋複製（SECURITY-OPERATIONS.md §18.15.9）：連線字串 ENC:v3、授權頁模式、Security DLL 轉接。
/// 2026-10-17 起因：桌機 WSL 整顆 vhdx 拿到筆電啟動，發布版與 dotnet run 照常登入可寫——
/// 有 Security DLL 時走 Adapter，主機比對整段沒有執行。
/// </summary>
public class LicenseHostBindingTests
{
    private const string Fp = "GOLDENFP";
    private const string Lic = "LIC-GOLDEN";
    private const string Uuid = "28b9a163-2724-0e4a-8012-c01850a62f38";

    // ── 連線字串加密 ────────────────────────────────────────────────────

    /// <summary>以改版前 tools/EncryptConnectionString（origin/main 5a2ea08）產生：既有 ENC:v2 密文必須照樣解得開。</summary>
    private const string GoldenV2 =
        "ENC:v2:unKFsK5VOIC8+QOhZdT1Bw==:PbiD3+hygTHvS7qI:TWbUMBEVhRJN9J9MxjOyAIOq0Xae2A==:kYaT7o230HNRzfDo0j89Ew==";

    [Fact]
    public void V2_GoldenVector_FromPreviousTool_StillDecrypts()
        => Assert.Equal("Host=golden;Password=x", ConnectionStringCipher.Decrypt(GoldenV2, Fp, Lic, null, hostBound: false));

    [Fact]
    public void V3_RoundTrips_OnlyWithTheSameHostUuid()
    {
        var enc = ConnectionStringCipher.Encrypt("Host=db;Password=s", Fp, Lic, Uuid);
        Assert.StartsWith(ConnectionStringCipher.V3Prefix, enc);
        Assert.Equal("Host=db;Password=s", ConnectionStringCipher.Decrypt(enc, Fp, Lic, Uuid, hostBound: true));

        // 複製品：指紋與授權編號都一樣（錨定檔與授權檔跟著磁碟走），只差主機板
        Assert.ThrowsAny<CryptographicException>(() =>
            ConnectionStringCipher.Decrypt(enc, Fp, Lic, "54d25bd8-0000-4000-8000-000000000001", hostBound: true));
    }

    [Fact]
    public void V3_WithoutHostUuid_IsRejected()
    {
        var enc = ConnectionStringCipher.Encrypt("x", Fp, Lic, Uuid);
        Assert.Throws<InvalidOperationException>(() => ConnectionStringCipher.Decrypt(enc, Fp, Lic, null, hostBound: false));
    }

    [Fact]
    public void HostBoundLicense_RejectsV2()
    {
        // 否則複製品沿用改版前的 ENC:v2 密文就繞過主機綁定
        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionStringCipher.Decrypt(GoldenV2, Fp, Lic, Uuid, hostBound: true));
        Assert.Contains("ENC:v3", ex.Message);
    }

    [Theory]
    [InlineData("ENC:v2:AAAA:AAAA:AAAA:AAAA")]   // 綁主機授權拒收 v2
    [InlineData("not-encrypted")]               // 格式錯誤
    public void RecryptHint_PointsToUserTool_NotMaintainerTool(string cipherText)
    {
        // 使用者手上只有 tools/EncryptConnectionString；LicenseAuthority 含私鑰、只在維護者端
        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionStringCipher.Decrypt(cipherText, Fp, Lic, Uuid, hostBound: true));
        Assert.Contains("tools/EncryptConnectionString", ex.Message);
        Assert.DoesNotContain("LicenseAuthority", ex.Message);
    }

    [Fact]
    public void V3_KeyIsNotTheV2Key()
    {
        // 把 v3 密文改標成 v2，用 v2 的金鑰材料（指紋＋授權編號）解不開
        var v3 = ConnectionStringCipher.Encrypt("x", Fp, Lic, Uuid);
        var relabeled = ConnectionStringCipher.V2Prefix + v3[ConnectionStringCipher.V3Prefix.Length..];
        Assert.ThrowsAny<CryptographicException>(() => ConnectionStringCipher.Decrypt(relabeled, Fp, Lic, null, hostBound: false));
    }

    // ── 主機識別碼原值 ──────────────────────────────────────────────────

    private sealed class WinProbe(string? lastConfig) : IHostProbe
    {
        public bool IsWindows => true;
        public bool IsLinux => false;
        public string? ReadFile(string path) => null;
        public bool FileExists(string path) => false;
        public string? ReadRegistry(string subKey, string valueName)
            => valueName == "LastConfig" ? lastConfig : valueName == "SystemManufacturer" ? "ASUSTeK" : "D900";
        public string? Run(string fileName, string arguments) => null;
    }

    [Fact]
    public void DetectWithUuid_ReturnsNormalizedRawUuid_MatchingTheHash()
    {
        var (host, uuid) = HostIdentity.DetectWithUuid(new WinProbe("{28B9A163-2724-0E4A-8012-C01850A62F38}"));
        Assert.Equal(Uuid, uuid);
        Assert.Equal(HostIdentity.HashUuid(Uuid), host.HostId);
        Assert.Equal(host, HostIdentity.Detect(new WinProbe("{28B9A163-2724-0E4A-8012-C01850A62F38}")));
    }

    [Fact]
    public void DetectWithUuid_Unreadable_HasNoUuid()
    {
        var (host, uuid) = HostIdentity.DetectWithUuid(new WinProbe(null));
        Assert.Null(uuid);
        Assert.Null(host.HostId);
        Assert.NotNull(host.Problem);
    }

    [Fact]
    public void HostIdentityResult_NeverCarriesTheRawUuid()
    {
        // record 的 ToString 會列出全部屬性——原值若放進去，任何一行記錄它的日誌都會外洩
        var (host, _) = HostIdentity.DetectWithUuid(new WinProbe(Uuid));
        Assert.DoesNotContain(Uuid, host.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── 授權頁模式 ──────────────────────────────────────────────────────

    [Fact]
    public void BlockedPage_ShowsReasonAndRequestCode_Encoded()
    {
        var html = LicenseBlockedHost.BuildPage(LicenseHostGate.Mismatch, "<b>不屬於</b>", "CODE-123", "https://x/register?code=CODE-123");
        Assert.Contains("此授權不屬於這台電腦", html);
        Assert.Contains("&lt;b&gt;不屬於&lt;/b&gt;", html);   // 訊息要編碼，不能當 HTML
        Assert.Contains("CODE-123", html);
        Assert.Contains("未連線資料庫", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);   // 沒有登入的頁面不提供上傳
    }

    [Fact]
    public void BlockedPage_Unavailable_PointsToTheReadFix()
    {
        var html = LicenseBlockedHost.BuildPage(LicenseHostGate.Unavailable, "請重開 WSL", null, null);
        Assert.Contains("無法確認本機是授權機器", html);
        Assert.Contains("MachineId", html);   // 錨定碼讀不到時改用工具取指紋
    }

    [Fact]
    public void BlockedPage_MessageOf_UnwrapsReflection()
    {
        var inner = LicenseHostGate.Blocked(LicenseHostGate.Mismatch, "真正的原因");
        Assert.Equal("真正的原因", LicenseBlockedHost.MessageOf(new TargetInvocationException(inner)));
    }

    // ── Security DLL 轉接 ───────────────────────────────────────────────

    /// <summary>模擬 DLL 的 SecurityContext（主程式只以反射讀它的公開成員）。</summary>
    public sealed class FakeDllContext
    {
        public string LicenseId => "LIC-X";
        public string ClientCode => "ACME";
        public string ClientName => "Acme";
        public DateTimeOffset ExpiresAt => LicenseSupport.PerpetualExpiry;
        public bool AllowPlainConnection => false;
        public string BindingMode => "offline";
        public int Seats => 1;
        public bool HostBound { get; init; }
        public string? HostDeploy { get; init; }
        public void ReValidate(string? licensePath) { }
    }

    /// <summary>改版前的 DLL：沒有主機成員。</summary>
    public sealed class OldDllContext
    {
        public string LicenseId => "LIC-X";
        public string ClientCode => "ACME";
        public DateTimeOffset ExpiresAt => LicenseSupport.PerpetualExpiry;
        public void ReValidate(string? licensePath) { }
    }

    [Fact]
    public void Adapter_CarriesHostResultFromDll_SoVmEnforcementWorks()
    {
        var ctx = new LicenseValidatorAdapter(new FakeDllContext { HostBound = true, HostDeploy = "vm" }, isProduction: true).Validate();
        Assert.Equal(HostCheckResult.Match, ctx.HostCheck);
        Assert.Equal(DeploymentKind.Vm, ctx.HostKind);

        var svc = new LicenseStateService(TimeProvider.System);
        svc.SetLicense(ctx);
        Assert.True(svc.LeaseRequired);
    }

    [Fact]
    public void Adapter_LicenseWithoutHosts_IsNotRequired()
    {
        var ctx = new LicenseValidatorAdapter(new FakeDllContext { HostBound = false }, isProduction: true).Validate();
        Assert.Equal(HostCheckResult.NotRequired, ctx.HostCheck);
        Assert.Equal(DeploymentKind.Unknown, ctx.HostKind);
    }

    [Fact]
    public void Adapter_RejectsOldDll_WithoutHostCheck()
    {
        // 複製出來的環境換上改版前的 DLL 就能繞過閘門
        var ex = Assert.Throws<SecurityException>(() => new LicenseValidatorAdapter(new OldDllContext(), isProduction: true).Validate());
        Assert.Contains("版本過舊", ex.Message);
    }
}
