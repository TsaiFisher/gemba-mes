using SmartFactory.MES.Services;
using System.Text.RegularExpressions;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// EnvironmentFingerprintService 單元測試（v2 錨定碼）。
///
/// <para>
/// Phase 1 與 Phase 2（Security DLL）共用同一份 <c>MachineAnchor</c> 原始檔，
/// 因此「兩者是否一致」已不需要靠測試保證 —— 它們編譯自同一段程式碼。
/// 本檔改為驗證演算法本身：黃金向量、格式、決定性，以及錨定碼缺席時的行為。
/// </para>
/// </summary>
[Collection(MachineAnchorEnvCollection.Name)]
public class EnvironmentFingerprintServiceTests
{
    private readonly EnvironmentFingerprintService _sut = new();

    public EnvironmentFingerprintServiceTests() => TestAnchor.Apply();

    // ── 演算法黃金向量 ────────────────────────────────────────────────────────

    [Fact]
    public void GetFingerprint_MatchesGoldenVector()
    {
        // 指紋 = SHA256(UTF8("v2:" + anchorId))
        // 此斷言釘住演算法本身：任何改動都會使所有既有授權失效，
        // 因此必須是刻意為之（連同 fingerprintAlgo 版本一起變更）。
        Assert.Equal(TestAnchor.ExpectedFingerprint, _sut.GetFingerprint());
    }

    // ── 基本格式 ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetFingerprint_Returns64CharHexString()
    {
        var fp = _sut.GetFingerprint();

        Assert.NotNull(fp);
        Assert.Equal(64, fp.Length); // SHA-256 = 32 bytes = 64 hex chars
        Assert.Matches(new Regex("^[0-9A-F]{64}$"), fp); // uppercase hex
    }

    [Fact]
    public void GetFingerprint_IsDeterministic()
    {
        var fp1 = _sut.GetFingerprint();
        var fp2 = _sut.GetFingerprint();

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GetFingerprint_TwoCallsNeverReturnDifferentValues()
    {
        // 多次呼叫結果一致，確保 License 比對不會因為隨機性而失敗
        var results = Enumerable.Range(0, 5)
            .Select(_ => _sut.GetFingerprint())
            .Distinct()
            .ToList();

        Assert.Single(results);
    }

    // ── 錨定碼取值順序 ────────────────────────────────────────────────────────

    [Fact]
    public void GetFingerprint_DifferentAnchor_ProducesDifferentFingerprint()
    {
        var original = _sut.GetFingerprint();
        try
        {
            Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", "ANOTHER-ANCHOR-9999");
            Assert.NotEqual(original, _sut.GetFingerprint());
        }
        finally
        {
            TestAnchor.Apply();
        }
    }

    [Fact]
    public void GetFingerprint_NoAnchorAnywhere_ThrowsWithActionableMessage()
    {
        // 錨定碼缺席時必須「明確失敗」而非自動產生一個新的：
        // 自動產生會讓「授權驗證失敗」偽裝成「悄悄換了一個身分」。
        try
        {
            Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", null);
            // 指向一個不存在的路徑，避免讀到本機真實的錨定檔
            Environment.SetEnvironmentVariable(
                "SMARTFACTORY_ANCHOR_PATH",
                Path.Combine(Path.GetTempPath(), $"no-such-anchor-{Guid.NewGuid():N}"));

            var ex = Assert.Throws<InvalidOperationException>(() => _sut.GetFingerprint());
            Assert.Contains("找不到機器錨定碼", ex.Message);
            Assert.Contains("--init", ex.Message);  // 訊息必須告訴使用者下一步怎麼做
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMARTFACTORY_ANCHOR_PATH", null);
            TestAnchor.Apply();
        }
    }
}
