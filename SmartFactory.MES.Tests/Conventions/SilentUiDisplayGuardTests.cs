using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions;

/// <summary>
/// 兩種「畫面上靜悄悄出錯」的寫法（2026-10-27 驗收回報）：
/// <list type="bullet">
/// <item><c>window.showToast</c>：全域沒有這個函式（crud-panel.js 提供的是 toastSuccess），
///   <c>?.</c> 呼叫等於什麼都不做——NCR「執行處置」失敗時訊息整個被吞掉，看起來像按了沒反應。</item>
/// <item>直接印 <c>@x.ApprovalStatus</c>：中文介面顯示 PendingApproval／Approved。一律走 <c>DocStatusLabels.Label</c>。</item>
/// </list>
/// </summary>
public class SilentUiDisplayGuardTests
{
    private static IEnumerable<string> Files(params string[] patterns)
    {
        var root = ConventionScan.RepoRoot();
        return new[] { "Modules", "Views", Path.Combine("wwwroot", "js"), Path.Combine("wwwroot", "mobile") }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => patterns.SelectMany(p => Directory.EnumerateFiles(d, p, SearchOption.AllDirectories)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(root, f));
    }

    [Fact]
    public void 不得呼叫不存在的_window_showToast()
    {
        var root = ConventionScan.RepoRoot();
        var hits = Files("*.cshtml", "*.js")
            .Where(f => File.ReadAllText(Path.Combine(root, f)).Contains("window.showToast"))
            .ToList();
        Assert.True(hits.Count == 0,
            "window.showToast 不存在（成功用 window.toastSuccess、失敗用 alert）：\n  " + string.Join("\n  ", hits));
    }

    private static readonly Regex RawApprovalStatus = new(@"@(?:\()?[A-Za-z_][\w.]*\.ApprovalStatus\)?\s*<", RegexOptions.Compiled);

    [Fact]
    public void 簽核狀態不得直接印enum名稱()
    {
        var root = ConventionScan.RepoRoot();
        var hits = Files("*.cshtml")
            .SelectMany(f => File.ReadAllLines(Path.Combine(root, f))
                .Select((line, i) => (f, i, line))
                .Where(x => RawApprovalStatus.IsMatch(x.line)))
            .Select(x => $"{x.f}:{x.i + 1}  {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0,
            "簽核狀態請用 DocStatusLabels.Label(...)，直接印會在中文介面顯示英文：\n  " + string.Join("\n  ", hits));
    }
}
