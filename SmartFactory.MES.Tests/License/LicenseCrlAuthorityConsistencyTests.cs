using System.Diagnostics;
using System.Security.Cryptography;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 跨實作一致性：LicenseAuthority 的 <c>revoke</c> 指令簽出來的撤銷清單，
/// 客戶端的 <see cref="LicenseCrl.ParseAndVerify"/> 必須驗得過。
///
/// <para>
/// 這是整個撤銷機制**最容易靜默出錯**的地方：簽章 payload 有兩份實作
/// （<c>LicenseAuthority/Program.cs</c> 的 <c>BuildCrlSignaturePayload</c> 與
/// <c>Services/LicenseCrl.cs</c> 的同名方法），任一邊改了欄位順序、型別或排序規則，
/// 客戶端只會回報「簽章無效」—— 一個指不出原因的訊息，而且要等到真的要撤銷
/// 某個客戶時才會發現。
/// </para>
///
/// <para>
/// 判斷條件寫成「LicenseAuthority 專案在不在」而不是「是不是在 CI」：
/// 該目錄含私鑰、整個在 .gitignore 內，任何 checkout 出來的樹都沒有它，
/// 只有簽發主機驗得到。把事實寫成環境判斷的後果見 §14.9 與 PR #33。
/// </para>
/// </summary>
public class LicenseCrlAuthorityConsistencyTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly bool AuthorityAvailable = File.Exists(Path.Combine(
        RepoRoot, "SmartFactory.MES.LicenseAuthority", "SmartFactory.MES.LicenseAuthority.csproj"));

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sfmes-crl-authority-" + Guid.NewGuid().ToString("N"));

    private readonly RSA _key = RSA.Create(2048);

    public LicenseCrlAuthorityConsistencyTests()
    {
        Directory.CreateDirectory(_dir);
        // 測試專用金鑰對：正式私鑰不在版控中，也不該被測試碰到。
        File.WriteAllText(PrivateKeyPath, _key.ExportRSAPrivateKeyPem());
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private string PrivateKeyPath => Path.Combine(_dir, "test-private.pem");
    private string CrlPath        => Path.Combine(_dir, "crl.json");

    [Fact]
    public void AuthorityRevoke_ProducesCrlThatClientAccepts()
    {
        if (!AuthorityAvailable) return; // 簽發主機以外沒有這個專案（見類別註解）

        Revoke("LIC-CONSISTENCY-001");

        var crl = LicenseCrl.ParseAndVerify(File.ReadAllText(CrlPath), _key);

        Assert.Equal(LicenseCrl.SupportedVersion, crl.Version);
        Assert.NotNull(LicenseCrl.FindRevocation(crl, "LIC-CONSISTENCY-001"));
        Assert.True(crl.NextUpdate > DateTimeOffset.UtcNow, "預設 nextUpdate 應在未來");
    }

    [Fact]
    public void AuthorityRevoke_SecondCall_AccumulatesInsteadOfOverwriting()
    {
        if (!AuthorityAvailable) return; // 簽發主機以外沒有這個專案（見類別註解）

        // 覆蓋而非累積的話，撤銷第二個客戶會讓第一個客戶靜靜恢復可用。
        Revoke("LIC-AAA");
        Revoke("LIC-BBB");

        var crl = LicenseCrl.ParseAndVerify(File.ReadAllText(CrlPath), _key);

        Assert.Equal(2, crl.Revoked.Length);
        Assert.NotNull(LicenseCrl.FindRevocation(crl, "LIC-AAA"));
        Assert.NotNull(LicenseCrl.FindRevocation(crl, "LIC-BBB"));
    }

    [Fact]
    public void AuthorityUnrevoke_RemovesOnlyThatEntry_AndStaysVerifiable()
    {
        if (!AuthorityAvailable) return; // 簽發主機以外沒有這個專案（見類別註解）

        Revoke("LIC-AAA");
        Revoke("LIC-BBB");
        Revoke("LIC-AAA", extraArgs: "--unrevoke");

        var crl = LicenseCrl.ParseAndVerify(File.ReadAllText(CrlPath), _key);

        Assert.Null(LicenseCrl.FindRevocation(crl, "LIC-AAA"));
        Assert.NotNull(LicenseCrl.FindRevocation(crl, "LIC-BBB"));
    }

    // ── 輔助 ─────────────────────────────────────────────────────────────────

    private void Revoke(string licenseId, string extraArgs = "")
    {
        var (exit, stdout, stderr) = Run(
            $"run --project SmartFactory.MES.LicenseAuthority -- revoke " +
            $"--license-id {licenseId} --private-key \"{PrivateKeyPath}\" --crl \"{CrlPath}\" {extraArgs}");

        Assert.True(exit == 0, $"revoke 失敗（exit {exit}）：\n{stdout}\n{stderr}");
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = arguments,
            WorkingDirectory       = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 dotnet");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        return (proc.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
    }
}
