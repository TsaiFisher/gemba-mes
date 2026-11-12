using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 換發授權匯入（SECURITY-OPERATIONS.md §18.8）：驗不過的檔案絕不能覆蓋現行授權。
/// 預檢驗證以委派替換（真的驗簽要用根私鑰簽的檔，測試環境沒有）。
/// </summary>
public sealed class LicenseImportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lic-import-").FullName;
    private string Target => Path.Combine(_dir, "license.json");
    private readonly LicenseStateService _state = new(TimeProvider.System);
    private readonly Mock<ILicenseValidator> _validator = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Lic(string id) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { licenseId = id }));
    private static LicenseContext Ctx(string id) => new() { LicenseId = id, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1), BindingMode = "offline" };

    private LicenseImportService Svc(Func<string, LicenseContext> precheck, bool licenseIdBound = false)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["License:Path"] = Target }).Build();
        return new LicenseImportService(cfg, _validator.Object, _state, NullLogger<LicenseImportService>.Instance, precheck, licenseIdBound);
    }

    [Fact]
    public void Valid_ReplacesLicense_KeepsBackup_UpdatesState()
    {
        File.WriteAllBytes(Target, Lic("OLD"));
        _validator.Setup(v => v.Validate()).Returns(Ctx("NEW"));

        var r = Svc(_ => Ctx("NEW")).Import(Lic("NEW"));

        Assert.True(r.Success);
        Assert.Contains("NEW", File.ReadAllText(Target));
        Assert.Single(Directory.GetFiles(_dir, "license.json.bak-*"));
        Assert.Equal("NEW", _state.License!.LicenseId);
        _validator.Verify(v => v.Validate(), Times.Once);   // 以正式驗證器重讀，不是只信預檢
    }

    [Fact]
    public void PrecheckFails_LeavesCurrentLicenseUntouched_AndNoTempFile()
    {
        File.WriteAllBytes(Target, Lic("OLD"));

        var r = Svc(_ => throw new SecurityException("授權檔案簽章無效")).Import(Lic("FORGED"));

        Assert.False(r.Success);
        Assert.Equal("Msg_LicenseImportRejected", r.MessageKey);
        Assert.Contains("簽章無效", r.Detail);
        Assert.Contains("OLD", File.ReadAllText(Target));
        Assert.Single(Directory.GetFiles(_dir));   // 沒有殘留暫存檔、沒有備份
        _validator.Verify(v => v.Validate(), Times.Never);
    }

    [Fact]
    public void FinalValidationFails_RestoresPreviousLicense()
    {
        // 預檢（主程式驗證器）過了，但正式驗證器（例如 Security DLL）不認
        File.WriteAllBytes(Target, Lic("OLD"));
        _validator.Setup(v => v.Validate()).Throws(new SecurityException("DLL 不認"));

        var r = Svc(_ => Ctx("NEW")).Import(Lic("NEW"));

        Assert.False(r.Success);
        Assert.Contains("OLD", File.ReadAllText(Target));
    }

    [Fact]
    public void LicenseIdBound_RejectsDifferentLicenseId()
    {
        File.WriteAllBytes(Target, Lic("OLD"));

        var r = Svc(_ => Ctx("NEW"), licenseIdBound: true).Import(Lic("NEW"));

        Assert.False(r.Success);
        Assert.Equal("Msg_LicenseImportLicenseIdChanged", r.MessageKey);
        Assert.Contains("OLD", File.ReadAllText(Target));
    }

    [Fact]
    public void LicenseIdBound_SameLicenseId_IsAllowed()
    {
        File.WriteAllBytes(Target, Lic("SAME"));
        _validator.Setup(v => v.Validate()).Returns(Ctx("SAME"));
        Assert.True(Svc(_ => Ctx("SAME"), licenseIdBound: true).Import(Lic("SAME")).Success);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void NotJson_IsRejected_WithoutTouchingDisk(string content)
    {
        var r = Svc(_ => throw new InvalidOperationException()).Import(Encoding.UTF8.GetBytes(content));
        Assert.Equal("Msg_LicenseImportInvalidFile", r.MessageKey);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void RequestCode_IsBase64Url_AndCarriesOnlyFingerprintTpmVersionDeployHostHash()
    {
        var fp = new string('A', 64);
        var hostHash = new string('C', 64);
        var code = LicenseRequestCode.Build(fp, hasTpm: false, "1.2.3",
            new HostIdentityResult(hostHash, DeploymentKind.Wsl, "wsl-reg", null));
        Assert.DoesNotContain('+', code);
        Assert.DoesNotContain('/', code);
        Assert.DoesNotContain('=', code);

        var b64 = code.Replace('-', '+').Replace('_', '/');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4)));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["fp", "tpm", "ver", "deploy", "hostId"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(fp, doc.RootElement.GetProperty("fp").GetString());
        Assert.Equal("wsl", doc.RootElement.GetProperty("deploy").GetString());
        Assert.Equal(hostHash, doc.RootElement.GetProperty("hostId").GetString());   // 雜湊，不是 UUID 原值
        Assert.Equal("https://x/register?code=" + code, LicenseRequestCode.RegisterUrl("https://x/", code));
        // 換發頁也帶申請碼：伺服器換發要有主機識別碼才核發得出綁主機的授權
        Assert.Equal("https://x/renew?code=" + code, LicenseRequestCode.RenewUrl("https://x/", code));
        Assert.Equal("https://x/renew", LicenseRequestCode.RenewUrl("https://x/"));   // 錨定檔讀不到時讓使用者貼上
    }
}
