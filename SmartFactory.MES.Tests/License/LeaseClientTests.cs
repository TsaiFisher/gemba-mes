using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 租約用戶端（SECURITY-OPERATIONS.md §18.6、§18.8）：回應驗簽、lease.json、伺服器清單退路、
/// 通知與狀態。以測試專用的根金鑰／從屬金鑰／委任憑證扮演伺服器（與線上伺服器同格式）。
/// </summary>
public sealed class LeaseClientTests : IDisposable
{
    private const string LicId = "LIC-LEASE-1";
    private static readonly string Fp = new('A', 64);
    private static readonly DateTimeOffset T0 = new(2027, 1, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly RSA _root = RSA.Create(2048);
    private readonly ECDsa _sub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Directory.CreateTempSubdirectory("lease-").FullName;
    private readonly ManualTime _time = new(T0);
    private readonly LicenseStateService _state;
    private readonly FakeServer _server = new();
    private JsonObject _delegation;

    public LeaseClientTests()
    {
        _state = new LicenseStateService(_time);
        _delegation = Delegation(_root, ["lease"]);
        File.WriteAllText(Path.Combine(_dir, "license.json"), "{\"licenseId\":\"" + LicId + "\",\"bindingMode\":\"lease\"}");
        _state.SetLicense(new LicenseContext { LicenseId = LicId, ExpiresAt = T0.AddYears(1), BindingMode = "lease" });
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private JsonObject Delegation(RSA signer, string[] purposes)
    {
        var cert = new DelegationCertificate(1, "delegation", purposes,
            Convert.ToBase64String(_sub.ExportSubjectPublicKeyInfo()), T0.AddDays(-1), T0.AddDays(300), null);
        var sig = Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(LicenseDocuments.BuildPayload(cert)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return JsonNode.Parse(JsonSerializer.Serialize(cert with { Signature = sig }, LicenseDocuments.JsonOptions))!.AsObject();
    }

    private string Signed(object payload, ECDsa? key = null)
    {
        var text = JsonSerializer.Serialize(payload);
        var sig = Convert.ToBase64String((key ?? _sub).SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));
        return new JsonObject { ["payload"] = text, ["signature"] = sig, ["delegation"] = _delegation.DeepClone() }.ToJsonString();
    }

    private string Lease(string token, int days = 30, string? fp = null, string lic = LicId) => Signed(new
    {
        type = "lease", version = 1, licenseId = lic, fingerprint = fp ?? Fp, token,
        issuedAt = _time.GetUtcNow(), notAfter = _time.GetUtcNow().AddDays(days), serverTime = _time.GetUtcNow(), status = "active",
    });

    private string Notice(string status) => Signed(new
    {
        type = "lease-notice", version = 1, licenseId = LicId, fingerprint = Fp, status, serverTime = _time.GetUtcNow(),
    });

    /// <summary>預設 direct：這組測試驗的是 MES 自己續租的路徑；relay（預設值）另有案例。</summary>
    private LeaseRenewer Renewer(string? serverUrl = "https://lease.test", string transport = "direct") => new(
        new HttpClient(_server),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["License:Path"] = Path.Combine(_dir, "license.json") }).Build(),
        new LeaseOptions { ServerUrl = serverUrl, EndpointsUrl = "https://raw.test/endpoints.json", Transport = transport },
        _state, _time, NullLogger<LeaseRenewer>.Instance, _root, () => Fp);

    // ── relay：MES 不連外，由瀏覽器代為確認（§18.15.3）────────────────────────

    [Fact]
    public void Options_DefaultTransport_IsRelay()
        => Assert.True(new LeaseOptions().IsRelay);

    [Fact]
    public async Task Relay_RenewAsync_NeverTouchesTheNetwork()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        Assert.Equal(LeaseOutcome.NotApplicable, await Renewer(transport: "relay").RenewAsync());
        Assert.Empty(_server.Bodies);
        Assert.True(_state.Current.IsWriteBlocked);
    }

    [Fact]
    public async Task Relay_CheckinUrl_CarriesRequestAfterHash_AndResponseIsAppliedLikeDirect()
    {
        var r = Renewer(transport: "relay");
        var req = (await r.BuildRequestAsync())!;
        var url = LeaseRenewer.CheckinUrl("https://lease.test/", req);
        Assert.StartsWith("https://lease.test/checkin#", url);
        var frag = url[(url.IndexOf('#') + 1)..].Replace('-', '+').Replace('_', '/');
        var decoded = JsonNode.Parse(Convert.FromBase64String(frag.PadRight(frag.Length + (4 - frag.Length % 4) % 4, '=')))!;
        Assert.Equal(Fp, decoded["fingerprint"]!.GetValue<string>());
        Assert.Equal(LicId, decoded["license"]!["licenseId"]!.GetValue<string>());

        Assert.Equal(LeaseOutcome.Leased, r.ApplyResponse(Lease("t1", days: 90)));
        Assert.True(File.Exists(LeaseFile));
        Assert.Equal(LicenseState.Normal, _state.Current.State);
        Assert.Equal("t1", JsonNode.Parse((await r.BuildRequestAsync())!.ToJsonString())!["token"]!.GetValue<string>());   // 下次帶新啟用碼
    }

    [Fact]
    public void Relay_ForgedResponseThroughBrowser_IsRejected_AndNothingWritten()
    {
        using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Signed(new
        {
            type = "lease", version = 1, licenseId = LicId, fingerprint = Fp, token = "x",
            issuedAt = T0, notAfter = T0.AddYears(5), serverTime = T0, status = "active",
        }, rogue);
        Assert.Equal(LeaseOutcome.Rejected, Renewer(transport: "relay").ApplyResponse(forged));
        Assert.False(File.Exists(LeaseFile));
        Assert.True(_state.Current.IsWriteBlocked);
    }

    [Fact]
    public void Relay_ServerOffline_UsesConfiguredThenDefault_WithoutFetching()
    {
        Assert.Equal("https://lease.test", Renewer(transport: "relay").ResolveServerOffline());
        Assert.Equal(LicenseRequestCode.DefaultServerUrl, Renewer(serverUrl: null, transport: "relay").ResolveServerOffline());
        Assert.Empty(_server.Bodies);
    }

    private string LeaseFile => Path.Combine(_dir, "lease.json");

    // ── offline 授權的線上確認（§18.15.8）──────────────────────────────────

    private void UseOffline(DeploymentKind kind, bool optIn = false)
    {
        _state.SetLicense(new LicenseContext { LicenseId = LicId, ExpiresAt = LicenseSupport.PerpetualExpiry, BindingMode = "offline",
                                               HostCheck = HostCheckResult.Match, HostKind = kind });
        _state.SetOnlineCheckOptIn(optIn);
    }

    [Fact]
    public async Task Offline_NotRequired_Direct_DoesNotConnectOut()
    {
        UseOffline(DeploymentKind.Physical);
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        Assert.Equal(LeaseOutcome.NotApplicable, await Renewer().RenewAsync());
        Assert.Empty(_server.Bodies);
    }

    [Fact]
    public async Task Offline_CanStillConfirmByRelay_BeforeTurningParamOn()
    {
        UseOffline(DeploymentKind.Physical);
        var r = Renewer(transport: "relay");
        Assert.NotNull(await r.BuildRequestAsync());
        Assert.Equal(LeaseOutcome.Leased, r.ApplyResponse(Lease("t1", days: 90)));
        Assert.Null(LicenseOnlineCheck.CheckChange(_state.Inputs, true, _time.GetUtcNow()));
    }

    [Theory]
    [InlineData(DeploymentKind.Vm, false)]
    [InlineData(DeploymentKind.Physical, true)]
    public async Task Offline_Required_Direct_Renews(DeploymentKind kind, bool optIn)
    {
        UseOffline(kind, optIn);
        _server.Respond(HttpStatusCode.OK, Lease("t1", days: 90));
        Assert.Equal(LeaseOutcome.Leased, await Renewer().RenewAsync());
        Assert.Single(_server.Bodies);
        Assert.Equal(LicenseState.Normal, _state.Current.State);
    }

    [Fact]
    public async Task Offline_ConfirmFails_NeverConfirmed_VmIsReadOnly_ButOptInOnlyWarns()
    {
        // 伺服器預設回 503（無簽章）：確認失敗
        UseOffline(DeploymentKind.Vm);
        Assert.NotEqual(LeaseOutcome.Leased, await Renewer().RenewAsync());
        Assert.True(_state.Current.IsWriteBlocked);

        UseOffline(DeploymentKind.Physical, optIn: true);
        Assert.NotEqual(LeaseOutcome.Leased, await Renewer().RenewAsync());
        Assert.Equal(LicenseState.Warning, _state.Current.State);
    }

    // ── 驗簽 ────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_ValidLease()
        => Assert.Equal("t1", LeaseDocuments.Verify(Lease("t1"), _root, LicId, Fp).Token);

    [Fact]
    public void Verify_TamperedPayload_Fails()
    {
        var doc = JsonNode.Parse(Lease("t1"))!.AsObject();
        doc["payload"] = doc["payload"]!.GetValue<string>().Replace("\"notAfter\":\"2027-02", "\"notAfter\":\"2029-02");
        Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(doc.ToJsonString(), _root, LicId, Fp));
    }

    [Fact]
    public void Verify_OtherMachinesLease_Fails()
        => Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(Lease("t1", fp: new string('B', 64)), _root, LicId, Fp));

    [Fact]
    public void Verify_OtherLicensesLease_Fails()
        => Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(Lease("t1", lic: "LIC-OTHER"), _root, LicId, Fp));

    [Fact]
    public void Verify_DelegationFromAnotherRoot_Fails()
    {
        _delegation = Delegation(RSA.Create(2048), ["lease"]);
        Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(Lease("t1"), _root, LicId, Fp));
    }

    [Fact]
    public void Verify_DelegationWithoutLeasePurpose_Fails()
    {
        _delegation = Delegation(_root, ["tpm-binding"]);
        Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(Lease("t1"), _root, LicId, Fp));
    }

    [Fact]
    public void Verify_SignedByKeyNotInDelegation_Fails()
    {
        using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<SecurityException>(() => LeaseDocuments.Verify(Signed(new
        {
            type = "lease", version = 1, licenseId = LicId, fingerprint = Fp, token = "x",
            issuedAt = T0, notAfter = T0.AddDays(30), serverTime = T0, status = "active",
        }, rogue), _root, LicId, Fp));
    }

    // ── 續租 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstLease_WritesLeaseFile_AndLiftsReadOnly()
    {
        Assert.True(_state.Current.IsWriteBlocked);   // lease 授權、尚未取得租約 → 唯讀
        _server.Respond(HttpStatusCode.OK, Lease("t1"));

        Assert.Equal(LeaseOutcome.Leased, await Renewer().RenewAsync());

        Assert.True(File.Exists(LeaseFile));
        Assert.Equal(LicenseState.Normal, _state.Current.State);
        var sent = JsonNode.Parse(_server.Bodies[0])!;
        Assert.Equal(Fp, sent["fingerprint"]!.GetValue<string>());
        Assert.Null(sent["token"]);
        Assert.Equal(LicId, sent["license"]!["licenseId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Renewal_SendsStoredToken()
    {
        File.WriteAllText(LeaseFile, Lease("t1"));
        _server.Respond(HttpStatusCode.OK, Lease("t2"));

        await Renewer().RenewAsync();

        Assert.Equal("t1", JsonNode.Parse(_server.Bodies[0])!["token"]!.GetValue<string>());
        Assert.Equal("t2", LeaseDocuments.Verify(File.ReadAllText(LeaseFile), _root, LicId, Fp).Token);
    }

    [Fact]
    public async Task TamperedStoredLease_IsIgnored_NoTokenSent()
    {
        File.WriteAllText(LeaseFile, Lease("t1").Replace("\\u0022t1\\u0022", "\\u0022zz\\u0022"));
        File.WriteAllText(LeaseFile, File.ReadAllText(LeaseFile).Replace("\"signature\":\"", "\"signature\":\"AA"));
        Assert.Null(Renewer().LoadStoredLease());
        _server.Respond(HttpStatusCode.OK, Lease("t2"));
        await Renewer().RenewAsync();
        Assert.Null(JsonNode.Parse(_server.Bodies[0])!["token"]);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("replaced")]
    public async Task SignedTerminalNotice_TurnsReadOnly_AndSurvivesLaterLease(string status)
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        _server.Respond(HttpStatusCode.Forbidden, Notice(status));

        Assert.Equal(LeaseOutcome.Notice, await Renewer().RenewAsync());
        Assert.Equal(LicenseState.ReadOnly, _state.Current.State);

        _server.Respond(HttpStatusCode.OK, Lease("t3"));   // 之後就算又拿到租約也不解除（單向加嚴）
        await Renewer().RenewAsync();
        Assert.True(_state.Current.IsWriteBlocked);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("seat-taken")]
    public async Task ConflictNotice_IsWarningOnly(string status)
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        _server.Respond(HttpStatusCode.Conflict, Notice(status));

        await Renewer().RenewAsync();

        // §18.8：conflict 不提前轉唯讀——被誤判的可能是原機
        Assert.Equal(LicenseState.Warning, _state.Current.State);
    }

    [Fact]
    public async Task UnsignedRejection_DoesNotChangeWriteState()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        _server.Respond(HttpStatusCode.Gone, "{\"error\":\"expired\"}");

        Assert.Equal(LeaseOutcome.Rejected, await Renewer().RenewAsync());
        Assert.False(_state.Current.IsWriteBlocked);
        Assert.Contains("expired", _state.Current.Reason);
    }

    [Fact]
    public async Task ForgedNotice_FromNonServer_IsIgnored()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.Respond(HttpStatusCode.Forbidden, Signed(new
        {
            type = "lease-notice", version = 1, licenseId = LicId, fingerprint = Fp, status = "revoked", serverTime = T0,
        }, rogue));

        await Renewer().RenewAsync();
        Assert.False(_state.Current.IsWriteBlocked);
    }

    [Fact]
    public async Task OfflineLicense_DoesNothing()
    {
        _state.SetLicense(new LicenseContext { LicenseId = LicId, ExpiresAt = T0.AddYears(1), BindingMode = "offline" });
        Assert.Equal(LeaseOutcome.NotApplicable, await Renewer().RenewAsync());
        Assert.Empty(_server.Bodies);
    }

    // ── 伺服器清單 ──────────────────────────────────────────────────────

    private string Endpoints(RSA signer, params string[] urls)
    {
        var d = new EndpointsDocument(1, "endpoints", T0, urls, null);
        var sig = Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(LicenseDocuments.BuildPayload(d)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return JsonSerializer.Serialize(d with { Signature = sig }, LicenseDocuments.JsonOptions);
    }

    [Fact]
    public async Task Endpoints_Signed_AreUsed_InOrder_AndCached()
    {
        _server.Get = _ => (HttpStatusCode.OK, Endpoints(_root, "https://a.test", "https://b.test"));
        Assert.Equal(["https://a.test", "https://b.test"], await Renewer(serverUrl: null).ResolveServersAsync());

        _server.Get = _ => (HttpStatusCode.ServiceUnavailable, "");   // GitHub 掛了 → 用快取
        Assert.Equal(["https://a.test", "https://b.test"], await Renewer(serverUrl: null).ResolveServersAsync());
    }

    [Fact]
    public async Task Endpoints_ForgedSignature_FallsBackToBuiltIn()
    {
        _server.Get = _ => (HttpStatusCode.OK, Endpoints(RSA.Create(2048), "https://evil.test"));
        Assert.Equal([LicenseRequestCode.DefaultServerUrl], await Renewer(serverUrl: null).ResolveServersAsync());
    }

    [Fact]
    public async Task FirstServerDown_TriesNext()
    {
        _server.Get = _ => (HttpStatusCode.OK, Endpoints(_root, "https://down.test", "https://up.test"));
        _server.Post = uri => uri.Host == "down.test" ? throw new HttpRequestException("down") : (HttpStatusCode.OK, Lease("t1"));
        Assert.Equal(LeaseOutcome.Leased, await Renewer(serverUrl: null).RenewAsync());
    }

    [Fact]
    public async Task AllServersDown_Unreachable_StateUnchanged()
    {
        _server.Post = _ => throw new HttpRequestException("down");
        Assert.Equal(LeaseOutcome.Unreachable, await Renewer().RenewAsync());
    }

    // ── 狀態與排程 ──────────────────────────────────────────────────────

    [Fact]
    public async Task LeaseExpiry_ReadOnly_ThenDisabledAfter30Days()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1", days: 30));
        await Renewer().RenewAsync();

        _time.SetUtcNow(T0.AddDays(17));
        Assert.Equal(LicenseState.Warning, _state.Current.State);     // 剩不到 14 天
        _time.SetUtcNow(T0.AddDays(30));
        Assert.Equal(LicenseState.ReadOnly, _state.Current.State);
        _time.SetUtcNow(T0.AddDays(60));
        Assert.Equal(LicenseState.Disabled, _state.Current.State);
    }

    [Fact]
    public async Task ShortLease_WarnsOnlyAfterHalfElapsed()
    {
        _server.Respond(HttpStatusCode.OK, Signed(new
        {
            type = "lease", version = 1, licenseId = LicId, fingerprint = Fp, token = "t1",
            issuedAt = T0, notAfter = T0.AddHours(2), serverTime = T0, status = "active",
        }));
        await Renewer().RenewAsync();

        Assert.Equal(LicenseState.Normal, _state.Current.State);     // 剛取得 2 小時租約，不該提醒
        _time.SetUtcNow(T0.AddMinutes(61));
        Assert.Equal(LicenseState.Warning, _state.Current.State);    // 過半仍未續上
        _time.SetUtcNow(T0.AddHours(2));
        Assert.Equal(LicenseState.ReadOnly, _state.Current.State);
    }

    [Fact]
    public async Task ClockRolledBack_MoreThanOneDay_IsReadOnly()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        _time.SetUtcNow(T0.AddDays(-2));
        Assert.Equal(LicenseState.ReadOnly, _state.Current.State);
        Assert.Contains("時鐘", _state.Current.Reason);
    }

    [Fact]
    public async Task ImportingDifferentLicense_DropsOldLease()
    {
        _server.Respond(HttpStatusCode.OK, Lease("t1"));
        await Renewer().RenewAsync();
        _state.SetLicense(new LicenseContext { LicenseId = "LIC-NEW", ExpiresAt = T0.AddYears(1), BindingMode = "lease" });
        Assert.True(_state.Current.IsWriteBlocked);   // 新授權還沒有租約
    }

    [Theory]
    [InlineData(LeaseOutcome.Leased, 0, 0.0, 24)]
    [InlineData(LeaseOutcome.Leased, 0, 1.0, 30)]
    [InlineData(LeaseOutcome.Unreachable, 1, 0.5, 1)]
    [InlineData(LeaseOutcome.Unreachable, 2, 0.5, 2)]
    [InlineData(LeaseOutcome.Unreachable, 3, 0.5, 4)]
    [InlineData(LeaseOutcome.Unreachable, 9, 0.5, 6)]
    [InlineData(LeaseOutcome.NotApplicable, 0, 0.5, 1)]
    public void Schedule(LeaseOutcome outcome, int failures, double jitter, double hours)
        => Assert.Equal(TimeSpan.FromHours(hours), LeaseRenewalService.NextDelay(outcome, failures, jitter));

    [Theory]
    [InlineData(2, 60)]          // 2 小時租約 → 1 小時後續
    [InlineData(0.1, 5)]         // 很短也至少隔 5 分鐘
    [InlineData(720, 24 * 60)]   // 30 天租約 → 照常每日（jitter 0）
    public void Schedule_ShortLease_RenewsAtHalfLife(double leaseHours, double expectMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectMinutes),
            LeaseRenewalService.NextDelay(LeaseOutcome.Leased, 0, 0, TimeSpan.FromHours(leaseHours)));

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Func<Uri, (HttpStatusCode, string)> Post { get; set; } = _ => (HttpStatusCode.ServiceUnavailable, "");
        public Func<Uri, (HttpStatusCode, string)> Get { get; set; } = _ => (HttpStatusCode.NotFound, "");
        public void Respond(HttpStatusCode code, string body) => Post = _ => (code, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Method == HttpMethod.Post) Bodies.Add(await req.Content!.ReadAsStringAsync(ct));
            var (code, body) = (req.Method == HttpMethod.Post ? Post : Get)(req.RequestUri!);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
