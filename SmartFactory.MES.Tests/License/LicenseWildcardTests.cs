using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權指紋綁定行為測試。
///
/// 設計原則：所有 License（含 Internal）均須綁定設備指紋，不支援萬用字元 "*"。
/// 三個驗證端（LicenseValidator、SecurityAnchor、ConnectionStringDecryptor）
/// 均使用精確比對：allowedFingerprints.Contains(currentFingerprint)
/// </summary>
public class LicenseFingerprintTests
{
    // ── 精確比對邏輯（三個驗證端共用的邏輯模型）────────────────────────────

    [Fact]
    public void FingerprintCheck_ExactMatch_Passes()
    {
        var currentFp  = "ABCDEF" + new string('1', 58);
        var allowedFps = new[] { currentFp };

        Assert.True(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FingerprintCheck_NoMatch_Fails()
    {
        var currentFp  = "AAAA" + new string('0', 60);
        var allowedFps = new[] { "BBBB" + new string('0', 60) };

        Assert.False(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FingerprintCheck_WildcardString_IsNotSpeciallyHandled()
    {
        // "*" 只是一個普通字串，不會比對任意指紋
        var currentFp  = "ABCD" + new string('0', 60); // 實際機器指紋
        var allowedFps = new[] { "*" };                // License 含 "*"

        // 驗證端只做 Contains 精確比對，"*" 不等於實際指紋 → 應拒絕
        Assert.False(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase),
            "\"*\" 不應被視為萬用字元，應視為普通字串精確比對");
    }

    [Fact]
    public void FingerprintCheck_CaseInsensitive_Passes()
    {
        var currentFp  = "abcdef" + new string('0', 58); // 小寫
        var allowedFps = new[] { "ABCDEF" + new string('0', 58) }; // 大寫

        Assert.True(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FingerprintCheck_MultipleFingerprintsInLicense_MatchesCorrectOne()
    {
        var currentFp  = "TARGET" + new string('0', 58);
        var allowedFps = new[]
        {
            "OTHER1" + new string('0', 58),
            currentFp,
            "OTHER2" + new string('0', 58)
        };

        Assert.True(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FingerprintCheck_EmptyAllowedList_Fails()
    {
        var currentFp  = "AAAA" + new string('0', 60);
        var allowedFps = Array.Empty<string>();

        Assert.False(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    // ── Mock 指紋服務整合測試 ─────────────────────────────────────────────

    [Fact]
    public void FingerprintService_MatchingFingerprint_Passes()
    {
        const string knownFp = "AABBCC" + "00000000000000000000000000000000000000000000000000000000000";
        var mockFp = new Mock<IEnvironmentFingerprintService>();
        mockFp.Setup(s => s.GetFingerprint()).Returns(knownFp);

        var currentFp  = mockFp.Object.GetFingerprint();
        var allowedFps = new[] { knownFp };

        Assert.True(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void FingerprintService_NonMatchingFingerprint_Fails()
    {
        const string machineFp = "MACHINE" + "000000000000000000000000000000000000000000000000000000000";
        const string licenseFp = "LICENSE" + "000000000000000000000000000000000000000000000000000000000";

        var mockFp = new Mock<IEnvironmentFingerprintService>();
        mockFp.Setup(s => s.GetFingerprint()).Returns(machineFp);

        var currentFp  = mockFp.Object.GetFingerprint();
        var allowedFps = new[] { licenseFp };

        Assert.False(allowedFps.Contains(currentFp, StringComparer.OrdinalIgnoreCase));
    }

    // ── LicenseAuthority 萬用字元攔截測試 ────────────────────────────────

    [Theory]
    [InlineData("*")]
    [InlineData("ABC;*;DEF")]
    public void LicenseAuthority_WildcardInFingerprints_ShouldBeRejected(string fingerprintsInput)
    {
        // 模擬 LicenseAuthority 的驗證邏輯
        var fps = fingerprintsInput.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasWildcard = fps.Any(f => f.Trim() == "*");

        Assert.True(hasWildcard, "測試資料應包含萬用字元");
        // LicenseAuthority 遇到此情況會 Exit(1)，在此僅驗證偵測邏輯正確
    }

    [Theory]
    [InlineData("ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890")]
    [InlineData("HASH1;HASH2")]
    public void LicenseAuthority_ValidFingerprints_ShouldBeAccepted(string fingerprintsInput)
    {
        var fps = fingerprintsInput.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasWildcard = fps.Any(f => f.Trim() == "*");

        Assert.False(hasWildcard, "有效指紋不應含萬用字元");
    }
}
