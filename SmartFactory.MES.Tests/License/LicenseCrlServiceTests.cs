using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// <see cref="LicenseCrlService"/> 的抓取／快取／寬限窗行為。
///
/// <para>
/// 全部不連網：以 <see cref="StubHandler"/> 取代 HTTP，用測試金鑰對簽章。
/// 這樣 CI 上也跑得到 —— 一個「只在有網路時才驗得到」的測試等於沒有測試。
/// </para>
/// </summary>
public class LicenseCrlServiceTests : IDisposable
{
    private const string Lic = "LIC-TEST-001";
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sfmes-crl-" + Guid.NewGuid().ToString("N"));

    public LicenseCrlServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清不掉不影響測試結果 */ }
        GC.SuppressFinalize(this);
    }

    private string CachePath => Path.Combine(_dir, "license-crl-cache.json");

    private LicenseCrlOptions Options(bool enabled = true, string url = "https://example.test/crl.json",
                                      int graceDays = 14)
        => new() { Enabled = enabled, Url = url, GraceDays = graceDays, CachePath = CachePath };

    private LicenseCrlService Service(LicenseCrlOptions options, StubHandler handler)
        => new(options, NullLogger<LicenseCrlService>.Instance, handler, LicenseCrlDocumentTests.Key);

    /// <summary>可控的 HTTP 樁：回傳固定內容，或模擬斷線。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public int Calls { get; private set; }

        public StubHandler(string body)
            : this(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }) { }

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public static StubHandler Offline() => new(() => throw new HttpRequestException("模擬斷線"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_respond());
        }
    }

    private static string SignedCrlJson(params CrlEntry[] revoked)
        => LicenseCrlDocumentTests.ToJson(
               LicenseCrlDocumentTests.Sign(LicenseCrlDocumentTests.Build(revoked)));

    private static CrlEntry Revocation(string licenseId = Lic)
        => new(licenseId, "contract-terminated", DateTimeOffset.UtcNow.AddDays(-1));

    // ── 未啟用 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_DoesNotCallNetwork()
    {
        // 離線廠區的預設狀態：不該有任何對外連線嘗試。
        var handler = new StubHandler(SignedCrlJson());
        var result  = await Service(Options(enabled: false), handler).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Disabled, result.Outcome);
        Assert.True(result.IsHealthy);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EnabledWithoutUrl_IsConfigErrorNotSilentPass()
    {
        var handler = new StubHandler(SignedCrlJson());
        var result  = await Service(Options(url: ""), handler).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Disabled, result.Outcome);
        Assert.Equal(0, handler.Calls);
    }

    // ── 正常路徑 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetched_NotRevoked_IsOk_AndCachesSuccess()
    {
        var result = await Service(Options(), new StubHandler(SignedCrlJson())).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Ok, result.Outcome);
        Assert.True(File.Exists(CachePath));

        var state = ReadState();
        Assert.NotNull(state.LastSuccessUtc);
    }

    [Fact]
    public async Task Fetched_Revoked_IsUnhealthy()
    {
        var result = await Service(Options(), new StubHandler(SignedCrlJson(Revocation()))).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Revoked, result.Outcome);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task Fetched_SomeoneElseRevoked_DoesNotAffectUs()
    {
        var result = await Service(Options(), new StubHandler(SignedCrlJson(Revocation("LIC-OTHER"))))
            .CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Ok, result.Outcome);
    }

    // ── 離線與快取 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Offline_FirstEverRun_IsStaleNotUnhealthy()
    {
        // 部署當天網路還沒開通，不該一啟動就轉紅。
        var result = await Service(Options(), StubHandler.Offline()).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Stale, result.Outcome);
        Assert.True(result.IsHealthy);
        Assert.True(File.Exists(CachePath));   // 寬限窗的計時起點已落地
    }

    [Fact]
    public async Task Offline_AfterRevocationWasSeen_StaysRevoked()
    {
        // 單向加嚴的端到端版本：先線上看到撤銷，之後拔網路仍然是 Revoked。
        var online = await Service(Options(), new StubHandler(SignedCrlJson(Revocation()))).CheckAsync(Lic);
        Assert.Equal(CrlOutcome.Revoked, online.Outcome);

        var offline = await Service(Options(), StubHandler.Offline()).CheckAsync(Lic);
        Assert.Equal(CrlOutcome.Revoked, offline.Outcome);
        Assert.False(offline.IsHealthy);
    }

    [Fact]
    public async Task Offline_BeyondGrace_IsUnhealthy()
    {
        SeedState(firstAttempt: DateTimeOffset.UtcNow.AddDays(-40),
                  lastSuccess:  DateTimeOffset.UtcNow.AddDays(-30));

        var result = await Service(Options(graceDays: 14), StubHandler.Offline()).CheckAsync(Lic);

        Assert.Equal(CrlOutcome.GraceExpired, result.Outcome);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task TamperedCache_IsDiscardedNotTrusted()
    {
        // 直接改快取檔加入一筆撤銷 —— 沒有私鑰就簽不出來，整份快取應被捨棄。
        SeedState(firstAttempt: DateTimeOffset.UtcNow.AddDays(-1),
                  lastSuccess:  DateTimeOffset.UtcNow.AddDays(-1),
                  crlJson: LicenseCrlDocumentTests.ToJson(
                      LicenseCrlDocumentTests.Sign(LicenseCrlDocumentTests.Build()) with
                      {
                          Revoked = [Revocation()]
                      }));

        var result = await Service(Options(), StubHandler.Offline()).CheckAsync(Lic);

        Assert.NotEqual(CrlOutcome.Revoked, result.Outcome);
        Assert.Equal(CrlOutcome.Stale, result.Outcome);
    }

    [Fact]
    public async Task ServerReturnsGarbage_IsTreatedAsNoInformation()
    {
        // 代理伺服器塞回一頁登入畫面是很常見的情況，不能被讀成「沒有人被撤銷」。
        SeedState(firstAttempt: DateTimeOffset.UtcNow.AddDays(-40),
                  lastSuccess:  DateTimeOffset.UtcNow.AddDays(-40));

        var result = await Service(Options(graceDays: 14), new StubHandler("<html>login</html>"))
            .CheckAsync(Lic);

        Assert.Equal(CrlOutcome.GraceExpired, result.Outcome);
    }

    [Fact]
    public async Task ExpiredCrl_DoesNotCountAsSuccessfulUpdate()
    {
        // 防重放：把一份合法但過期的清單釘在本機代理上，不能讓寬限窗永遠重置。
        SeedState(firstAttempt: DateTimeOffset.UtcNow.AddDays(-40),
                  lastSuccess:  DateTimeOffset.UtcNow.AddDays(-30));

        var expired = LicenseCrlDocumentTests.Sign(
            LicenseCrlDocumentTests.Build() with
            {
                IssuedAt   = DateTimeOffset.UtcNow.AddDays(-60),
                NextUpdate = DateTimeOffset.UtcNow.AddDays(-30)
            });

        var result = await Service(Options(graceDays: 14), new StubHandler(LicenseCrlDocumentTests.ToJson(expired)))
            .CheckAsync(Lic);

        Assert.Equal(CrlOutcome.GraceExpired, result.Outcome);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task ExpiredCrl_StillRevokes()
    {
        // 過期只影響「算不算一次成功更新」，不影響它所陳述的撤銷事實。
        var expired = LicenseCrlDocumentTests.Sign(
            LicenseCrlDocumentTests.Build(Revocation()) with
            {
                IssuedAt   = DateTimeOffset.UtcNow.AddDays(-60),
                NextUpdate = DateTimeOffset.UtcNow.AddDays(-30)
            });

        var result = await Service(Options(), new StubHandler(LicenseCrlDocumentTests.ToJson(expired)))
            .CheckAsync(Lic);

        Assert.Equal(CrlOutcome.Revoked, result.Outcome);
    }

    // ── 快取檔輔助 ───────────────────────────────────────────────────────────

    private LicenseCrlService.CrlState ReadState()
        => JsonSerializer.Deserialize<LicenseCrlService.CrlState>(
               File.ReadAllText(CachePath), LicenseCrl.JsonOptions)!;

    private void SeedState(DateTimeOffset firstAttempt, DateTimeOffset? lastSuccess, string? crlJson = null)
    {
        var state = new LicenseCrlService.CrlState
        {
            FirstAttemptUtc = firstAttempt,
            LastSuccessUtc  = lastSuccess,
            Crl = crlJson is null
                ? null
                : JsonSerializer.Deserialize<CrlDocument>(crlJson, LicenseCrl.JsonOptions)
        };
        File.WriteAllText(CachePath, JsonSerializer.Serialize(state, LicenseCrl.JsonOptions));
    }
}
