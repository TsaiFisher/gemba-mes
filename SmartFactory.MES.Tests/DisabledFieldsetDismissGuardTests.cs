using System.Text.RegularExpressions;
using SmartFactory.MES.Tests.Services;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 唯讀面板的「關閉」鈕不可放在 disabled 的 fieldset 裡。
///
/// <para>唯讀（檢視）模式常用 <c>&lt;fieldset disabled&gt;</c> 一次鎖住所有欄位，但 disabled
/// fieldset 會連裡面的<b>按鈕</b>一起停用——包括 <c>data-bs-dismiss</c> 的關閉鈕，
/// 於是「檢視」打開後關不掉（2026-10-05 發退料單回報）。頁尾要放在 fieldset 之外。</para>
/// </summary>
public class DisabledFieldsetDismissGuardTests
{
    [Fact]
    public void Disabled的fieldset裡不可有關閉鈕()
    {
        var root = ManualPaths.RepoRoot();
        var files = new[] { "Modules", "Views", "Areas" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cshtml", SearchOption.AllDirectories))
            .ToList();
        Assert.True(files.Count > 100, $"只掃到 {files.Count} 個 cshtml，掃描路徑已失效。");

        var offenders = new List<string>();
        var probed = 0;
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"<fieldset[^>]*disabled[^>]*>"))
            {
                probed++;
                var end = text.IndexOf("</fieldset>", m.Index + m.Length, StringComparison.Ordinal);
                if (end < 0) continue;
                if (text[(m.Index + m.Length)..end].Contains("data-bs-dismiss", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, f)}:{text[..m.Index].Count(c => c == '\n') + 1}");
            }
        }
        Assert.True(probed >= 20, $"只找到 {probed} 個 disabled fieldset，比對方式已失效。");
        Assert.True(offenders.Count == 0,
            "下列唯讀面板的關閉鈕在 disabled fieldset 內，檢視時按不了：\n  " + string.Join("\n  ", offenders));
    }
}
