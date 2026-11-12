using System.Text.RegularExpressions;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 授權訊息的「該找誰」文案守門。
///
/// <para><b>守的是什麼：</b>原本 16 處訊息一律寫死「請聯繫 SmartFactory」——使用者通常
/// 是透過導入顧問拿到系統，那是一句他看不懂、也不知道要去哪的話，而且出現在他最需要
/// 指引的時刻。</para>
///
/// <para><b>為什麼要用掃原始碼的方式守：</b>這類文案的回歸不會讓任何測試變紅，也不會
/// 讓建置失敗——它只會讓某一類使用者卡住，而那類使用者通常不會回報，他只是走掉。
/// 唯一抓得到的方法就是在原始碼層級禁止那個字串再出現。</para>
/// </summary>
public class LicenseSupportMessageGuardTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
    }

    /// <summary>
    /// 把使用者導向單一廠商的說法。<c>LicenseSupport.cs</c> 自己是唯一的例外
    /// （文案就放在那裡），<c>LicenseAuthority</c> 也不算——那支工具只有核發方會用。
    /// </summary>
    private static readonly Regex VendorOnlyHint = new(
        @"(聯繫|連繫|洽詢|傳送給|提供.{0,4}給|向)\s*SmartFactory|SmartFactory\s*(取得|申請|續約)|smartfactory\.tw",
        RegexOptions.Compiled);

    private static IEnumerable<string> ProductSources()
    {
        var root = RepoRoot();
        string[] scan = ["Services", "Controllers", "Middleware", "SmartFactory.MES.MachineId", "tools"];

        foreach (var rel in scan)
        {
            var dir = Path.Combine(root, rel);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var ext = Path.GetExtension(f);
                if (ext is not (".cs" or ".sh")) continue;
                if (Path.GetFileName(f) == "LicenseSupport.cs") continue;   // 文案本體
                yield return f;
            }
        }
        var program = Path.Combine(root, "Program.cs");
        if (File.Exists(program)) yield return program;
    }

    [Fact]
    public void 授權訊息不得把使用者導向單一廠商()
    {
        var offenders = new List<string>();

        foreach (var file in ProductSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // 註解裡提到廠商是說明，不是給使用者看的訊息
                var t = line.TrimStart();
                if (t.StartsWith("//") || t.StartsWith("///") || t.StartsWith("*") || t.StartsWith("#")) continue;
                if (VendorOnlyHint.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}　{line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "下列訊息把使用者導向單一廠商，透過顧問導入的使用者看到會不知道要去哪。\n" +
            "請改用 LicenseSupport.HowToObtain（或 RenewShort）：\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void 文案只給受管授權一條路()
    {
        // 社群授權已取消（SECURITY-OPERATIONS.md §18.10）；訊息不得再指向它
        Assert.Contains("授權提供者", LicenseSupport.HowToObtain);
        Assert.Contains("github.com/TsaiFisher/smartfactory-licensing", LicenseSupport.HowToObtain);
        Assert.DoesNotContain("社群", LicenseSupport.HowToObtain);
    }

    [Fact]
    public void 日誌短版不得含網址()
    {
        // 到期提醒每 24 小時寫一次，塞網址進去只會洗版
        Assert.DoesNotContain("http", LicenseSupport.RenewShort);
    }

    [Fact]
    public void Shell備援腳本的文案必須與常數同步()
    {
        // get-fingerprint.sh 是「沒有 .NET runtime」時的備援，引用不到 C# 常數，
        // 只能手動同步。兩份複本漂移的症狀是「兩個工具給出不同的申請管道」。
        var sh = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "get-fingerprint.sh"));

        Assert.Contains("github.com/TsaiFisher/smartfactory-licensing", sh);
        Assert.Contains("授權提供者", sh);
        Assert.DoesNotContain("社群", sh);
    }
}
