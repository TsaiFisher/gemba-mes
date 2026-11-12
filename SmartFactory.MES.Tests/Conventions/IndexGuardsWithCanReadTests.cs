using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions;

/// <summary>
/// 列表頁（<c>Index</c>）一律以 <c>CanRead</c> 守門，不得用 <c>CanCreate</c>。
///
/// **為什麼這值得一支測試**：`CanRead` 與 `CanCreate` 是 `MenuPermissions` 上兩個
/// **各自獨立**的旗標（`PermissionService` 逐個查，沒有任何蘊含關係），
/// 而側邊選單是用 `CanRead` 過濾的（`SidebarMenuViewComponent`）。
/// 兩者不一致的後果是最難查的那一種：**選單看得到，點下去 403**——
/// 使用者會回報「權限設了沒用」，而權限設定畫面上看起來完全正常。
///
/// 2026-09-02 實測 ProjectZ 庫：`管理人員` 對 APInvoice／ARInvoice／APPayment／
/// ARReceipt／BankAccount／AccountingPeriod／MonthEnd／NrvAssessment 八支正是
/// 「有 CanRead、沒有 CanCreate」，也就是這 8 支對它全是 403。
/// FIN／ACT 共 10 支 Controller 當時都寫成 `CanCreate`，已一併改正。
///
/// 掃的是**原始碼**而不是行為：行為測試要為每一支 Controller 各建一組角色與權限列，
/// 成本高且容易只覆蓋到幾支；而這條規則的違反形狀就是原始碼上的一個字。
/// </summary>
public class IndexGuardsWithCanReadTests
{
    [Fact]
    public void 列表頁一律以CanRead守門不得用CanCreate()
    {
        var root = ConventionScan.RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*Controller.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (!rel.StartsWith("Modules/") && !rel.StartsWith("Controllers/")) continue;

            var src = File.ReadAllText(file);

            // Index action 的簽章 → 其後第一個權限守門
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(src,
                @"public\s+async\s+Task<IActionResult>\s+Index\s*\((?:[^{]|\n)*?\{(?<body>(?:.|\n){0,600})"))
            {
                scanned++;
                var body = m.Groups["body"].Value;
                var guard = Regex.Match(body, @"_permissionService\.Can(?<kind>Read|Create)\(MenuKey\)\)\s*return\s+Forbid");
                if (guard.Success && guard.Groups["kind"].Value == "Create")
                    offenders.Add(rel);
            }
        }

        Assert.True(scanned >= 30,
            $"只掃到 {scanned} 個 Index action——正規式八成失效了，規則形同關閉。");

        Assert.True(offenders.Count == 0,
            "以下列表頁用 CanCreate 守門。列表是讀取動作，而側邊選單是用 CanRead 過濾的，"
            + "於是「有讀取權、沒有建立權」的角色會看得到選單卻點出 403：\n  "
            + string.Join("\n  ", offenders.Distinct()));
    }
}
