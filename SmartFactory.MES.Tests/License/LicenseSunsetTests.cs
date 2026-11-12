using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 終止聲明（SECURITY-OPERATIONS.md §18.9）：只有根金鑰簽章有效的 sunset.json 能解除控管；
/// 連不上、404、偽造一律不解除（否則擋住伺服器就等於解鎖）。
/// </summary>
[Collection(LicenseWatchdogCollection.Name)]
public sealed class LicenseSunsetTests : IDisposable
{
    private readonly RSA _root = RSA.Create(2048);
    private readonly string _dir = Directory.CreateTempSubdirectory("sunset-").FullName;
    private readonly LicenseStateService _state = new(TimeProvider.System);
    private IConfiguration Cfg => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["License:Path"] = Path.Combine(_dir, "license.json"), ["License:SunsetUrl"] = "https://raw.test/sunset.json" }).Build();
    private string SunsetFile => Path.Combine(_dir, "sunset.json");

    public LicenseSunsetTests()
        => _state.SetLicense(new LicenseContext { LicenseId = "L1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(-60), BindingMode = "offline" });

    public void Dispose() => Directory.Delete(_dir, true);

    private string Sunset(RSA signer, string scope = "all")
    {
        var d = new SunsetDocument(1, "sunset", scope, DateTimeOffset.UtcNow, null);
        var sig = Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(LicenseDocuments.BuildPayload(d)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return JsonSerializer.Serialize(d with { Signature = sig }, LicenseDocuments.JsonOptions);
    }

    private LicenseSunsetChecker Checker(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new Stub(respond)), Cfg, _state, NullLogger<LicenseSunsetChecker>.Instance, _root);

    [Fact]
    public void Local_Missing_IsNotActive() => Assert.False(LicenseSunset.IsActive(Cfg, _root));

    [Fact]
    public void Local_Valid_IsActive()
    {
        File.WriteAllText(SunsetFile, Sunset(_root));
        Assert.True(LicenseSunset.IsActive(Cfg, _root));
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("scope")]
    [InlineData("garbage")]
    public void Local_Invalid_IsNotActive(string kind)
    {
        File.WriteAllText(SunsetFile, kind switch
        {
            "forged" => Sunset(RSA.Create(2048)),
            "scope" => Sunset(_root, "plant-A"),
            _ => "{ not json",
        });
        Assert.False(LicenseSunset.IsActive(Cfg, _root));
    }

    [Fact]
    public async Task Remote_404_DoesNothing()
    {
        Assert.False(await Checker(() => new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync());
        Assert.Equal(LicenseState.Disabled, _state.Current.State);   // 60 天前到期 → 停用，沒被解除
        Assert.False(File.Exists(SunsetFile));
    }

    [Fact]
    public async Task Remote_Unreachable_NeverUnlocks()
    {
        Assert.False(await Checker(() => throw new HttpRequestException("blocked by firewall")).CheckAsync());
        Assert.NotEqual(LicenseState.Unlocked, _state.Current.State);
    }

    [Fact]
    public async Task Remote_Forged_IsIgnored()
    {
        var forged = Sunset(RSA.Create(2048));
        Assert.False(await Checker(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(forged) }).CheckAsync());
        Assert.NotEqual(LicenseState.Unlocked, _state.Current.State);
        Assert.False(File.Exists(SunsetFile));
    }

    [Fact]
    public async Task Remote_Valid_UnlocksPermanently_AndPersistsLocally()
    {
        var valid = Sunset(_root);
        Assert.True(await Checker(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(valid) }).CheckAsync());

        Assert.Equal(LicenseState.Unlocked, _state.Current.State);
        Assert.False(_state.Current.IsWriteBlocked);
        Assert.True(LicenseSunset.IsActive(Cfg, _root));   // 之後斷網重啟仍解除
    }

    [Fact]
    public async Task Watchdog_AfterSunset_DoesNotValidateLicense()
    {
        _state.SetSunset();
        var validator = new Mock<ILicenseValidator>();
        validator.Setup(v => v.Validate()).Throws(new FileNotFoundException("license.json 已不存在"));
        var sp = new ServiceCollection().AddSingleton(validator.Object).AddSingleton<ILicenseStateService>(_state).BuildServiceProvider();
        var env = new Mock<IHostEnvironment>();
        var wd = new LicenseWatchdogService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<LicenseWatchdogService>.Instance, env.Object);

        await wd.ValidateAsync();

        Assert.True(LicenseWatchdogService.IsHealthy);
        validator.Verify(v => v.Validate(), Times.Never);
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond());
    }
}
