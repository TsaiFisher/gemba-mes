using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 根金鑰簽章的授權文件：endpoints.json、sunset.json、委任憑證（SECURITY-OPERATIONS.md §18.4、§18.9）。
/// 以測試專用 RSA 金鑰簽，驗證端的公鑰由參數傳入。
/// </summary>
public class LicenseDocumentsTests
{
    private static readonly RSA Root = RSA.Create(2048);
    private static readonly RSA OtherRoot = RSA.Create(2048);
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static string Sign(string payload, RSA key) => Convert.ToBase64String(
        key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    private static string Json<T>(T doc) => JsonSerializer.Serialize(doc, LicenseDocuments.JsonOptions);

    private static SunsetDocument Sunset(string scope = "all", RSA? key = null)
    {
        var d = new SunsetDocument(1, "sunset", scope, T0, null);
        return d with { Signature = Sign(LicenseDocuments.BuildPayload(d), key ?? Root) };
    }

    private static EndpointsDocument Endpoints(params string[] urls)
    {
        var d = new EndpointsDocument(1, "endpoints", T0, urls, null);
        return d with { Signature = Sign(LicenseDocuments.BuildPayload(d), Root) };
    }

    private static DelegationCertificate Delegation(
        string[]? purposes = null, int days = 365, ECDsa? sub = null)
    {
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = sub ?? fresh;
        var d = new DelegationCertificate(
            1, "delegation", purposes ?? ["lease"],
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            T0, T0.AddDays(days), null);
        return d with { Signature = Sign(LicenseDocuments.BuildPayload(d), Root) };
    }

    // ── sunset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sunset_Valid_Parses()
    {
        var d = LicenseDocuments.ParseSunset(Json(Sunset()), Root);
        Assert.Equal("all", d.Scope);
    }

    [Fact]
    public void Sunset_SignedByAnotherKey_IsRejected()
        => Assert.Throws<SecurityException>(
            () => LicenseDocuments.ParseSunset(Json(Sunset(key: OtherRoot)), Root));

    [Fact]
    public void Sunset_Unsigned_IsRejected()
        => Assert.Throws<SecurityException>(
            () => LicenseDocuments.ParseSunset(Json(Sunset() with { Signature = null }), Root));

    [Fact]
    public void Sunset_UnknownScope_IsNotTreatedAsAll()
    {
        // 未來的部分範圍聲明，舊版客戶端必須當成「不適用」而不是「全部解除」
        var ex = Assert.Throws<InvalidOperationException>(
            () => LicenseDocuments.ParseSunset(Json(Sunset(scope: "plant-A")), Root));
        Assert.Contains("plant-A", ex.Message);
    }

    [Fact]
    public void Sunset_FutureVersion_ReportsVersion_NotTampering()
    {
        var d = Sunset() with { Version = 2 };
        var ex = Assert.Throws<InvalidOperationException>(() => LicenseDocuments.ParseSunset(Json(d), Root));
        Assert.Contains("版本", ex.Message);
    }

    [Fact]
    public void AnotherDocumentType_CannotBeReplayedAsSunset()
    {
        // 拿一份合法簽章的 endpoints.json 改 type 冒充終止聲明：type 在簽章內，必定驗不過
        var ep = Endpoints("https://a.example.workers.dev");
        var forged = new SunsetDocument(1, "sunset", "all", ep.IssuedAt, ep.Signature);
        Assert.Throws<SecurityException>(() => LicenseDocuments.ParseSunset(Json(forged), Root));

        // 原封不動拿去當終止聲明讀：type 不符，連驗簽都不必
        Assert.Throws<InvalidOperationException>(() => LicenseDocuments.ParseSunset(Json(ep), Root));
    }

    [Fact]
    public void Sunset_Garbage_Throws()
        => Assert.ThrowsAny<Exception>(() => LicenseDocuments.ParseSunset("not json", Root));

    // ── endpoints ──────────────────────────────────────────────────────────

    [Fact]
    public void Endpoints_Valid_KeepsOrder()
    {
        var d = LicenseDocuments.ParseEndpoints(
            Json(Endpoints("https://b.example.workers.dev", "https://a.example.workers.dev")), Root);
        Assert.Equal(["https://b.example.workers.dev", "https://a.example.workers.dev"], d.LeaseServers);
    }

    [Fact]
    public void Endpoints_ReorderedServers_BreakSignature()
    {
        // 順序是「依序嘗試」的語意，必須在簽章內
        var d = Endpoints("https://b.example.workers.dev", "https://a.example.workers.dev");
        var swapped = d with { LeaseServers = [d.LeaseServers[1], d.LeaseServers[0]] };
        Assert.Throws<SecurityException>(() => LicenseDocuments.ParseEndpoints(Json(swapped), Root));
    }

    [Theory]
    [InlineData("http://a.example.workers.dev")]
    [InlineData("a.example.workers.dev")]
    public void Endpoints_NonHttps_IsRejected(string url)
        => Assert.Throws<SecurityException>(() => LicenseDocuments.ParseEndpoints(Json(Endpoints(url)), Root));

    [Fact]
    public void Endpoints_Empty_IsRejected()
        => Assert.Throws<SecurityException>(() => LicenseDocuments.ParseEndpoints(Json(Endpoints()), Root));

    // ── 委任憑證 ───────────────────────────────────────────────────────────

    [Fact]
    public void Delegation_Valid_ReturnsSubKey_ThatVerifiesSubSignatures()
    {
        using var sub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cert = LicenseDocuments.ParseDelegation(Json(Delegation(sub: sub)));

        using var verified = LicenseDocuments.VerifyDelegation(cert, Root, "lease", T0.AddDays(10));

        var data = Encoding.UTF8.GetBytes("lease-payload");
        Assert.True(verified.VerifyData(data, sub.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Delegation_PurposeNotGranted_IsRejected()
        => Assert.Throws<SecurityException>(
            () => LicenseDocuments.VerifyDelegation(Delegation(["lease"]), Root, "tpm-binding", T0));

    [Fact]
    public void Delegation_UnknownPurpose_IsRejected()
        => Assert.Throws<SecurityException>(
            () => LicenseDocuments.VerifyDelegation(Delegation(["lease", "issue-license"]), Root, "lease", T0));

    [Theory]
    [InlineData(-1)]     // notBefore 之前
    [InlineData(366)]    // notAfter 之後
    public void Delegation_OutsideValidity_IsRejected(int dayOffset)
        => Assert.Throws<SecurityException>(
            () => LicenseDocuments.VerifyDelegation(Delegation(), Root, "lease", T0.AddDays(dayOffset)));

    [Fact]
    public void Delegation_LongerThanOneYear_IsRejected_EvenIfRootSigned()
    {
        // 上限在驗證端：核發工具寫錯也不會產生 5 年的從屬金鑰授權
        Assert.Throws<SecurityException>(
            () => LicenseDocuments.VerifyDelegation(Delegation(days: 400), Root, "lease", T0.AddDays(1)));
    }

    [Fact]
    public void Delegation_NonP256Key_IsRejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<SecurityException>(
            () => LicenseDocuments.VerifyDelegation(Delegation(sub: p384), Root, "lease", T0.AddDays(1)));
    }

    [Fact]
    public void Delegation_SwappedSubKey_BreaksSignature()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cert = Delegation() with
        {
            SubPublicKey = Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo())
        };
        Assert.Throws<SecurityException>(() => LicenseDocuments.VerifyDelegation(cert, Root, "lease", T0));
    }

    // ── payload 字面（LicenseAuthority 必須產生同一字串）───────────────────

    [Fact]
    public void Payloads_HavePinnedLiterals()
    {
        Assert.Equal(
            "{\"version\":1,\"type\":\"sunset\",\"scope\":\"all\",\"issuedAt\":\"2026-10-08T00:00:00+00:00\"}",
            LicenseDocuments.BuildPayload(new SunsetDocument(1, "sunset", "all", T0, "ignored")));

        Assert.Equal(
            "{\"version\":1,\"type\":\"endpoints\",\"issuedAt\":\"2026-10-08T00:00:00+00:00\"," +
            "\"leaseServers\":[\"https://b\",\"https://a\"]}",
            LicenseDocuments.BuildPayload(new EndpointsDocument(1, "endpoints", T0, ["https://b", "https://a"], "x")));

        Assert.Equal(
            "{\"version\":1,\"type\":\"delegation\",\"purposes\":[\"lease\",\"tpm-binding\"],\"subPublicKey\":\"K\"," +
            "\"notBefore\":\"2026-10-08T00:00:00+00:00\",\"notAfter\":\"2027-10-08T00:00:00+00:00\"}",
            LicenseDocuments.BuildPayload(new DelegationCertificate(
                1, "delegation", ["tpm-binding", "lease"], "K", T0, T0.AddYears(1), "x")));
    }
}
