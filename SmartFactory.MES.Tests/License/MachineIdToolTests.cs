using System.Diagnostics;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// MachineId 工具的 CLI 行為測試。
///
/// <para>
/// 這支工具是**唯一交到客戶手上的可執行檔**，而它的輸出正是客戶會截圖回傳的東西。
/// 因此這裡守的不只是「算得對」（那是 <see cref="FingerprintConsistencyTests"/> 的事），
/// 而是「印出來的東西可以安全外傳」。
/// </para>
///
/// <para>
/// 錨定碼等同金鑰材料——它同時是連線字串加密的金鑰來源。指紋是 SHA256(raw)，
/// 它存在的意義就是讓客戶能回報身分而不必交出金鑰。工具若把 raw 印在同一個畫面上，
/// 那層雜湊就白做了。
/// </para>
/// </summary>
[Collection(MachineAnchorEnvCollection.Name)]
public class MachineIdToolTests
{
    private static readonly string ProjectDir = Path.Combine(FindRepoRoot(), "SmartFactory.MES.MachineId");

    public MachineIdToolTests() => TestAnchor.Apply();

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

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

    /// <summary>
    /// 執行工具並回傳輸出。<paramref name="withoutAnchor"/> 為 true 時，
    /// 以**子程序專屬**的環境變數切斷錨定碼來源——不動本程序，
    /// 否則同 collection 的其他測試會拿到「沒有錨定碼」而間歇性假紅。
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunTool(
        string arguments = "", bool withoutAnchor = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{ProjectDir}\" -- {arguments}".TrimEnd(),
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (withoutAnchor)
        {
            // 清空第一順位，並把路徑指向不存在的檔案。CandidatePaths() 在有明確路徑時
            // 只回傳該路徑、不回退預設位置，因此這樣就確定取不到錨定碼。
            psi.Environment["SMARTFACTORY_INSTANCE_ID"] = "";
            psi.Environment["SMARTFACTORY_ANCHOR_PATH"] =
                Path.Combine(Path.GetTempPath(), "no-such-anchor-" + Guid.NewGuid().ToString("N"));
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        return (proc.ExitCode, stdout, stderr);
    }

    // ── 測試：輸出不得夾帶金鑰材料 ───────────────────────────────────────────

    /// <summary>
    /// 預設輸出不得含錨定碼原文。這是本檔存在的主要理由：
    /// 客戶被要求把這個畫面傳給原廠，畫面上就不能有等同密碼的東西。
    /// </summary>
    [Fact]
    public void DefaultOutput_DoesNotLeakTheAnchorItself()
    {
        var (exitCode, stdout, stderr) = RunTool();

        Assert.True(exitCode == 0, $"工具執行失敗（exit {exitCode}）：\n{stderr}");
        Assert.DoesNotContain(TestAnchor.Id, stdout);
    }

    /// <summary>
    /// 但指紋本身一定要還在——否則「不外洩」會以「什麼都不印」的方式達成，
    /// 那樣客戶就拿不到要回報的東西了。
    /// </summary>
    [Fact]
    public void DefaultOutput_StillCarriesTheFingerprint()
    {
        var (exitCode, stdout, stderr) = RunTool();

        Assert.True(exitCode == 0, $"工具執行失敗（exit {exitCode}）：\n{stderr}");
        Assert.Contains(TestAnchor.ExpectedFingerprint, stdout);
    }

    /// <summary>
    /// 診斷時仍需要看得到 raw，因此 --raw 要留著——與 get-fingerprint.sh --raw 對齊。
    /// </summary>
    [Fact]
    public void RawFlag_ShowsTheAnchor_ForDiagnostics()
    {
        var (exitCode, stdout, stderr) = RunTool("--raw");

        Assert.True(exitCode == 0, $"工具執行失敗（exit {exitCode}）：\n{stderr}");
        Assert.Contains(TestAnchor.Id, stdout);
        // 指紋不因診斷模式而改變
        Assert.Contains(TestAnchor.ExpectedFingerprint, stdout);
    }

    /// <summary>
    /// 全新安裝還沒有授權時系統起不來、進不了「授權狀態」頁，申請碼只能從這支工具取得。
    /// 申請碼要能被 LicenseAuthority <c>--request</c> 解開，指紋要與畫面上的一致，
    /// 且同樣不得夾帶錨定碼原文（CI runner 讀不到主機板 UUID，hostId 可能是 null，這裡不要求）。
    /// </summary>
    [Fact]
    public void DefaultOutput_CarriesRequestCode_WithFingerprintButNotAnchor()
    {
        var (exitCode, stdout, stderr) = RunTool();

        Assert.True(exitCode == 0, $"工具執行失敗（exit {exitCode}）：\n{stderr}");
        var lines = stdout.Split('\n', StringSplitOptions.TrimEntries);
        var at = Array.FindIndex(lines, l => l.StartsWith("申請碼"));
        Assert.True(at >= 0 && at + 1 < lines.Length, $"輸出沒有申請碼：\n{stdout}");
        var code = lines[at + 1];

        var b64 = code.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(b64));
        Assert.Equal(TestAnchor.ExpectedFingerprint, doc.RootElement.GetProperty("fp").GetString());
        Assert.True(doc.RootElement.TryGetProperty("deploy", out _));
        Assert.DoesNotContain(TestAnchor.Id, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        Assert.Contains("/register?code=" + code, stdout);
    }

    // ── 測試：客戶第一次執行的情境 ───────────────────────────────────────────

    /// <summary>
    /// --help 必須在「還沒有錨定碼」時也能顯示。
    /// 客戶第一次拿到工具就是這個狀態，若說明本身需要錨定碼才印得出來，
    /// 那份說明就永遠看不到——而它正是告訴客戶怎麼產生錨定碼的地方。
    /// </summary>
    [Fact]
    public void Help_WorksBeforeAnyAnchorExists()
    {
        var (exitCode, stdout, stderr) = RunTool("--help", withoutAnchor: true);

        Assert.True(exitCode == 0, $"--help 失敗（exit {exitCode}）：\n{stderr}");
        Assert.Contains("--init", stdout);
    }

    /// <summary>
    /// 沒有錨定碼時，顯示指紋必須明確失敗（exit 非 0）並說明怎麼辦，
    /// 不能靜靜印出一個算得出來的東西——那會變成「悄悄換了一個身分」。
    /// </summary>
    [Fact]
    public void Show_WithoutAnchor_FailsLoudly()
    {
        var (exitCode, stdout, _) = RunTool(withoutAnchor: true);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain(TestAnchor.ExpectedFingerprint, stdout);
    }
}
