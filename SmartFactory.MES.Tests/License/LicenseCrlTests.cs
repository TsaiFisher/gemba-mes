using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權撤銷清單（CRL）的文件格式與狀態判定測試。
///
/// <para>
/// 這裡用**測試專用的金鑰對**簽章：正式簽發私鑰在 LicenseAuthority 目錄下、
/// 不進版控，測試組件簽不出合法清單。<c>LicenseCrlService</c> 的 internal 建構式
/// 因此可以換掉公鑰 —— 正式路徑（public 建構式）永遠只用嵌入的 license-public.pem。
/// </para>
/// </summary>
public class LicenseCrlDocumentTests
{
    internal static readonly RSA Key = RSA.Create(2048);

    internal static CrlDocument Sign(CrlDocument crl, RSA? key = null)
    {
        var k = key ?? Key;
        var sig = Convert.ToBase64String(k.SignData(
            Encoding.UTF8.GetBytes(LicenseCrl.BuildSignaturePayload(crl)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return crl with { Signature = sig };
    }

    internal static CrlDocument Build(params CrlEntry[] revoked) => new(
        Version: LicenseCrl.SupportedVersion,
        IssuedAt: DateTimeOffset.UtcNow,
        NextUpdate: DateTimeOffset.UtcNow.AddDays(30),
        Revoked: revoked,
        Signature: string.Empty);

    internal static string ToJson(CrlDocument crl)
        => JsonSerializer.Serialize(crl, LicenseCrl.JsonOptions);

    // ── 驗簽 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseAndVerify_ValidSignature_Succeeds()
    {
        var crl = Sign(Build(new CrlEntry("LIC-A", "contract-terminated", DateTimeOffset.UtcNow)));

        var parsed = LicenseCrl.ParseAndVerify(ToJson(crl), Key);

        Assert.Single(parsed.Revoked);
        Assert.Equal("LIC-A", parsed.Revoked[0].LicenseId);
    }

    [Fact]
    public void ParseAndVerify_EntryAddedAfterSigning_Rejected()
    {
        // 這是撤銷清單最直接的攻擊：把自己從清單裡拿掉，或替別人加進去。
        var crl    = Sign(Build(new CrlEntry("LIC-A", "x", DateTimeOffset.UtcNow)));
        var forged = crl with { Revoked = [] };

        Assert.Throws<SecurityException>(() => LicenseCrl.ParseAndVerify(ToJson(forged), Key));
    }

    [Fact]
    public void ParseAndVerify_NextUpdateExtendedAfterSigning_Rejected()
    {
        // 延長 nextUpdate 可讓一份舊清單永遠看起來是新的 —— 它在簽章 payload 內。
        var crl    = Sign(Build());
        var forged = crl with { NextUpdate = crl.NextUpdate.AddYears(10) };

        Assert.Throws<SecurityException>(() => LicenseCrl.ParseAndVerify(ToJson(forged), Key));
    }

    [Fact]
    public void ParseAndVerify_SignedByDifferentKey_Rejected()
    {
        using var attacker = RSA.Create(2048);
        var crl = Sign(Build(new CrlEntry("LIC-A", "x", DateTimeOffset.UtcNow)), attacker);

        Assert.Throws<SecurityException>(() => LicenseCrl.ParseAndVerify(ToJson(crl), Key));
    }

    [Fact]
    public void ParseAndVerify_NoSignature_Rejected()
    {
        Assert.Throws<SecurityException>(() => LicenseCrl.ParseAndVerify(ToJson(Build()), Key));
    }

    [Fact]
    public void ParseAndVerify_EntryOrderDoesNotMatter()
    {
        // payload 以 licenseId 排序，因此檔案中的順序不影響驗簽結果。
        var a = new CrlEntry("LIC-A", "x", DateTimeOffset.UtcNow);
        var b = new CrlEntry("LIC-B", "y", DateTimeOffset.UtcNow);

        var signed   = Sign(Build(a, b));
        var reordered = signed with { Revoked = [b, a] };

        var parsed = LicenseCrl.ParseAndVerify(ToJson(reordered), Key);
        Assert.Equal(2, parsed.Revoked.Length);
    }

    [Fact]
    public void ParseAndVerify_UnknownVersion_FailsWithVersionMessage_NotSignatureMessage()
    {
        // 版本檢查必須排在驗簽之前。順序顛倒的話，未來版本的清單會得到
        // 「簽章無效、可能已被竄改」—— 一個指向錯誤方向的訊息（§14.9 同一個教訓）。
        var future = Build() with { Version = LicenseCrl.SupportedVersion + 1, Signature = "not-a-signature" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => LicenseCrl.ParseAndVerify(ToJson(future), Key));

        Assert.Contains("版本", ex.Message);
    }

    [Fact]
    public void ParseAndVerify_Garbage_ThrowsNotReturnsEmpty()
    {
        // 解析失敗必須丟例外。若靜靜回傳一份空清單，任何壞掉的回應都會被
        // 讀成「沒有人被撤銷」。
        Assert.ThrowsAny<Exception>(() => LicenseCrl.ParseAndVerify("<html>404</html>", Key));
    }

    // ── 撤銷查詢 ─────────────────────────────────────────────────────────────

    [Fact]
    public void FindRevocation_IsCaseInsensitive()
    {
        var crl = Build(new CrlEntry("LIC-ACME-001", "x", DateTimeOffset.UtcNow));

        Assert.NotNull(LicenseCrl.FindRevocation(crl, "lic-acme-001"));
    }

    [Fact]
    public void FindRevocation_OtherLicense_NotAffected()
    {
        var crl = Build(new CrlEntry("LIC-A", "x", DateTimeOffset.UtcNow));

        Assert.Null(LicenseCrl.FindRevocation(crl, "LIC-B"));
    }
}

/// <summary>
/// 狀態判定。<see cref="LicenseCrl.Evaluate"/> 是純函式，時間由測試傳入，
/// 因此「寬限窗第 15 天」這種情境不必真的等 15 天，也不會因為執行時機而閃紅。
/// </summary>
public class LicenseCrlEvaluateTests
{
    private const string Lic = "LIC-A";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    private static CrlDocument Revoking(string licenseId) => new(
        Version: 1, IssuedAt: Now, NextUpdate: Now.AddDays(30),
        Revoked: [new CrlEntry(licenseId, "contract-terminated", Now.AddDays(-1))],
        Signature: "sig");

    private static CrlDocument Empty() => new(
        Version: 1, IssuedAt: Now, NextUpdate: Now.AddDays(30),
        Revoked: [], Signature: "sig");

    [Fact]
    public void Fetched_NotRevoked_IsOk()
    {
        var r = LicenseCrl.Evaluate(Empty(), fetchedNow: true, Now, Now.AddDays(-100), Lic, 14, Now);

        Assert.Equal(CrlOutcome.Ok, r.Outcome);
        Assert.True(r.IsHealthy);
    }

    [Fact]
    public void Fetched_Revoked_IsUnhealthy()
    {
        var r = LicenseCrl.Evaluate(Revoking(Lic), fetchedNow: true, Now, Now, Lic, 14, Now);

        Assert.Equal(CrlOutcome.Revoked, r.Outcome);
        Assert.False(r.IsHealthy);
        Assert.Contains(Lic, r.Message);
    }

    [Fact]
    public void CachedRevocation_SurvivesNetworkOutage()
    {
        // 單向加嚴的核心：把網路拔掉不會讓已撤銷的授權復活。
        var r = LicenseCrl.Evaluate(
            Revoking(Lic), fetchedNow: false,
            lastSuccessUtc: Now.AddDays(-3), anchorUtc: Now.AddDays(-100),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.Revoked, r.Outcome);
        Assert.False(r.IsHealthy);
    }

    [Fact]
    public void RevocationWins_EvenAfterGraceExpired()
    {
        // 撤銷的訊息比「連不上」的訊息更有用，順序不能顛倒。
        var r = LicenseCrl.Evaluate(
            Revoking(Lic), fetchedNow: false,
            lastSuccessUtc: Now.AddDays(-90), anchorUtc: Now.AddDays(-90),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.Revoked, r.Outcome);
    }

    [Fact]
    public void NotFetched_WithinGrace_IsStaleButHealthy()
    {
        // 產線系統不因外部網路故障停擺。
        var r = LicenseCrl.Evaluate(
            Empty(), fetchedNow: false,
            lastSuccessUtc: Now.AddDays(-5), anchorUtc: Now.AddDays(-100),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.Stale, r.Outcome);
        Assert.True(r.IsHealthy);
        Assert.Contains("寬限還剩", r.Message);
    }

    [Fact]
    public void NotFetched_BeyondGrace_IsUnhealthy()
    {
        var r = LicenseCrl.Evaluate(
            Empty(), fetchedNow: false,
            lastSuccessUtc: Now.AddDays(-15), anchorUtc: Now.AddDays(-100),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.GraceExpired, r.Outcome);
        Assert.False(r.IsHealthy);
    }

    [Fact]
    public void NeverFetched_BeyondGraceFromFirstAttempt_IsUnhealthy()
    {
        // 從第一天就把網址擋掉的客戶：沒有 lastSuccess 可以起算，
        // 改由「首次嘗試時間」起算，否則封鎖網址等於永久停用這道檢查。
        var r = LicenseCrl.Evaluate(
            crl: null, fetchedNow: false,
            lastSuccessUtc: null, anchorUtc: Now.AddDays(-20),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.GraceExpired, r.Outcome);
        Assert.False(r.IsHealthy);
    }

    [Fact]
    public void NeverFetched_WithinGraceFromFirstAttempt_IsStale()
    {
        // 剛部署、還沒連上過 —— 不該立刻轉紅。
        var r = LicenseCrl.Evaluate(
            crl: null, fetchedNow: false,
            lastSuccessUtc: null, anchorUtc: Now.AddDays(-2),
            Lic, graceDays: 14, now: Now);

        Assert.Equal(CrlOutcome.Stale, r.Outcome);
        Assert.True(r.IsHealthy);
    }

    [Fact]
    public void NoCrlAvailable_IsNeverTreatedAsRevoked()
    {
        // 沒有資訊 ≠ 被撤銷。反過來（沒有資訊 ＝ 放行）則由寬限窗限制時效。
        var r = LicenseCrl.Evaluate(
            crl: null, fetchedNow: false,
            lastSuccessUtc: Now.AddDays(-1), anchorUtc: Now.AddDays(-1),
            Lic, graceDays: 14, now: Now);

        Assert.NotEqual(CrlOutcome.Revoked, r.Outcome);
    }
}
