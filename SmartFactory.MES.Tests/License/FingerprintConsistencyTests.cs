using System.Diagnostics;
using System.Text.RegularExpressions;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 跨實作指紋一致性測試。
///
/// 驗證以下實作產生的指紋值完全相同：
///   1. EnvironmentFingerprintService（Phase 1 ASP.NET Core 服務，透過 MachineAnchor）
///   2. MachineId 工具（交付客戶取指紋用，自 #164 起本專案也有）
///   3. get-fingerprint.sh（Bash 備援腳本）
///
/// 前兩者共用同一份 MachineAnchor 原始檔（以 Link 引入），理論上不可能不同；
/// 這些測試真正在擋的是 (a) 有人把共用改回各自複製、(b) Bash 腳本與 C# 的
/// 錨定碼取值順序不一致 —— 後者會導致「客戶回報指紋 A，app 卻算出指紋 B」。
///
/// 注意：與 LicenseAuthority CLI 的比對僅存在於 Core repo
/// （SmartFactory.MES.LicenseAuthority 含私鑰與簽發功能，整個目錄在 .gitignore
/// 排除，刻意不隨部署 / 顧問 repo 出貨）。因此本專案沒有那一組測試——
/// 這是「那個專案不在這裡」的結果，不是「這裡不驗」。
/// </summary>
[Collection(MachineAnchorEnvCollection.Name)]
public class FingerprintConsistencyTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly Regex FingerprintPattern = new(@"^[0-9A-F]{64}$");

    /// <summary>
    /// 以環境變數提供固定錨定碼。子程序（bash）會繼承，
    /// 因此兩個實作拿到的是同一個輸入，測試結果不受執行環境有無錨定檔影響。
    /// </summary>
    public FingerprintConsistencyTests() => TestAnchor.Apply();

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        // 從目前執行目錄往上找到 SmartFactory.MES.sln
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string filename, string arguments, string? workingDir = null, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = filename,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"無法啟動程序：{filename}");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);

        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 從程式輸出中擷取 64 字元大寫 Hex 指紋值。
    /// 兩個工具的輸出都會包含指紋作為獨立一行。
    /// </summary>
    private static string? ExtractFingerprint(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (FingerprintPattern.IsMatch(trimmed))
                return trimmed;
        }
        return null;
    }

    // ── 測試：EnvironmentFingerprintService vs get-fingerprint.sh ────────────
    // （與 LicenseAuthority CLI 的比對為 Core repo 專屬，見類別註解）

    [Fact]
    public void GetFingerprintSh_MatchesEnvironmentFingerprintService()
    {
        // Arrange
        var service = new EnvironmentFingerprintService();
        var serviceFp = service.GetFingerprint();

        // Act: 執行 Bash 腳本
        var scriptPath = Path.Combine(RepoRoot, "tools", "get-fingerprint.sh");
        Assert.True(File.Exists(scriptPath), $"找不到腳本：{scriptPath}");

        var (exitCode, stdout, stderr) = RunProcess("bash", $"\"{scriptPath}\"");

        // Assert
        Assert.True(exitCode == 0,
            $"get-fingerprint.sh 執行失敗（exit {exitCode}）：\n{stderr}");

        var shFp = ExtractFingerprint(stdout);
        Assert.NotNull(shFp);
        Assert.Equal(serviceFp, shFp);
    }

    [Fact]
    public void GetFingerprintSh_Returns64CharUpperHex()
    {
        var scriptPath = Path.Combine(RepoRoot, "tools", "get-fingerprint.sh");
        Assert.True(File.Exists(scriptPath), $"找不到腳本：{scriptPath}");

        var (exitCode, stdout, _) = RunProcess("bash", $"\"{scriptPath}\"");

        Assert.Equal(0, exitCode);

        var fp = ExtractFingerprint(stdout);
        Assert.NotNull(fp);
        Assert.Equal(64, fp!.Length);
        Assert.Matches(FingerprintPattern, fp);
    }

    // ── 測試：MachineId 工具（客戶實際使用的取指紋管道）────────────────────

    /// <summary>
    /// 沒有任何跳過條件：此工具在版控裡（本專案自 #164 起也有了），CI 也建置它。
    /// 它是客戶實際拿到的取指紋管道，沒有比它更該被守住的東西。
    /// </summary>
    [Fact]
    public void MachineIdTool_MatchesEnvironmentFingerprintService()
    {
        var serviceFp = new EnvironmentFingerprintService().GetFingerprint();

        var project = Path.Combine(RepoRoot, "SmartFactory.MES.MachineId");
        var (exitCode, stdout, stderr) = RunProcess(
            "dotnet", $"run --project \"{project}\"");

        Assert.True(exitCode == 0, $"MachineId 工具執行失敗（exit {exitCode}）：\n{stderr}");

        var toolFp = ExtractFingerprint(stdout);
        Assert.NotNull(toolFp);
        Assert.Equal(serviceFp, toolFp);
    }

    // ── 測試：黃金向量（釘住演算法）──────────────────────────────────────

    [Fact]
    public void GetFingerprintSh_MatchesGoldenVector()
    {
        var scriptPath = Path.Combine(RepoRoot, "tools", "get-fingerprint.sh");
        Assert.True(File.Exists(scriptPath), $"找不到腳本：{scriptPath}");

        var (exitCode, stdout, stderr) = RunProcess("bash", $"\"{scriptPath}\"");
        Assert.True(exitCode == 0, $"get-fingerprint.sh 執行失敗：\n{stderr}");

        Assert.Equal(TestAnchor.ExpectedFingerprint, ExtractFingerprint(stdout));
    }

    [Fact]
    public void GetFingerprintSh_NoAnchor_FailsLoudly()
    {
        // 錨定碼缺席時腳本必須以非 0 結束，而不是印出一個「看起來正常」的指紋。
        // v1 的 no-uuid 正是後者：fallback 全滅仍照常輸出，直到授權失效才被發現。
        var scriptPath = Path.Combine(RepoRoot, "tools", "get-fingerprint.sh");

        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment.Remove("SMARTFACTORY_INSTANCE_ID");
        psi.Environment["SMARTFACTORY_ANCHOR_PATH"] =
            Path.Combine(Path.GetTempPath(), $"no-such-anchor-{Guid.NewGuid():N}");

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        Assert.NotEqual(0, proc.ExitCode);
        Assert.Null(ExtractFingerprint(stdout));
        Assert.Contains("找不到機器錨定碼", stderr);
    }
}
