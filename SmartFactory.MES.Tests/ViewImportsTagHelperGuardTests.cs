using SmartFactory.MES.Tests.Services;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 用到 <c>asp-*</c> 的 Razor 檔，所在資料夾（或上層）一定要有 <c>@addTagHelper</c> 的 _ViewImports。
///
/// <para>沒有的話不會編譯錯也不會執行錯：<c>asp-action="Print"</c> 原樣輸出成一個沒有意義的 HTML 屬性，
/// 連結沒有 href、按了沒反應；表單沒有 action、也沒有防偽 token。2026-10-05 回報資產負債表、損益表
/// 的列印與匯出無效，根因是 Modules/RPT/Views 整個模組少了這個檔。</para>
/// </summary>
public class ViewImportsTagHelperGuardTests
{
    [Fact]
    public void 用到asp屬性的畫面_其資料夾鏈上必有啟用TagHelper的ViewImports()
    {
        var root = ManualPaths.RepoRoot();
        var views = new[] { "Modules", "Views", "Areas", "Pages" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cshtml", SearchOption.AllDirectories))
            .Where(f => !Path.GetFileName(f).StartsWith("_ViewImports", StringComparison.Ordinal))
            .ToList();
        Assert.True(views.Count > 200, $"只掃到 {views.Count} 個 cshtml，掃描路徑已失效。");

        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (var f in views)
        {
            if (!File.ReadAllText(f).Contains(" asp-", StringComparison.Ordinal)) continue;
            checkedCount++;
            if (!HasTagHelperImport(Path.GetDirectoryName(f)!, root))
                offenders.Add(Path.GetRelativePath(root, f));
        }
        Assert.True(checkedCount > 100, $"只找到 {checkedCount} 個用到 asp-* 的畫面，比對方式已失效。");
        Assert.True(offenders.Count == 0,
            "下列畫面用了 asp-* 但所在資料夾鏈上沒有 @addTagHelper，屬性會原樣輸出、連結與表單失效：\n  "
            + string.Join("\n  ", offenders.Take(30)));
    }

    private static bool HasTagHelperImport(string dir, string root)
    {
        for (var d = dir; d != null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
        {
            var vi = Path.Combine(d, "_ViewImports.cshtml");
            if (File.Exists(vi) && File.ReadAllText(vi).Contains("@addTagHelper", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
