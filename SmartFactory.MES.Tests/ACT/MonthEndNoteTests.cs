using System.Text.RegularExpressions;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Tests.Conventions;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>月結備註的系統產生段改存代碼、顯示時依語系還原（英文介面原本整欄中文）。</summary>
public class MonthEndNoteTests
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["Note_NrvIncrease"] = "NRV write-down {0} (increase {1})",
        ["Note_Skipped"]     = "[Skipped] {0}",
    };

    [Fact]
    public void Render_換成當下語系_參數依序套入()
    {
        var notes = MonthEndNote.Token("Note_NrvIncrease", "JE-1", 216.5m) + "; " + MonthEndNote.Token("Note_Skipped", "n/a");
        Assert.Equal("NRV write-down JE-1 (increase 216.5); [Skipped] n/a", MonthEndNote.Render(notes, k => En.GetValueOrDefault(k)));
    }

    [Fact]
    public void Render_使用者文字與改版前的中文備註照原文_找不到的代碼原樣保留()
    {
        Assert.Equal("已鎖定 3 筆工單成本為 Final。", MonthEndNote.Render("已鎖定 3 筆工單成本為 Final。", k => En.GetValueOrDefault(k)));
        Assert.Equal("[[Note_Unknown|x]]", MonthEndNote.Render("[[Note_Unknown|x]]", k => En.GetValueOrDefault(k)));
    }

    [Fact]
    public void Token_參數裡的分隔符號不會破壞格式()
    {
        var t = MonthEndNote.Token("Note_Skipped", "a|b]]c");
        Assert.Equal("[Skipped] a/b))c", MonthEndNote.Render(t, k => En.GetValueOrDefault(k)));
    }

    /// <summary>程式裡用到的每個 Note_ 代碼，中英兩份 resx 都要有格式——少一份，那個語系就直接露出代碼。</summary>
    [Fact]
    public void 程式用到的每個備註代碼_中英resx都有()
    {
        var root = ConventionScan.RepoRoot();
        var code = string.Concat(new[] { "MonthEndService.cs", "PeriodEndAdjustmentService.cs" }
            .Select(f => File.ReadAllText(Path.Combine(root, "Modules", "ACT", "Services", f))));
        var keys = Regex.Matches(code, @"\$?""Note_(\{noteKey\})?(\w*)""")
            .SelectMany(m => m.Groups[1].Success ? new[] { "Nrv", "Ecl" }.Select(p => "Note_" + p + m.Groups[2].Value) : new[] { "Note_" + m.Groups[2].Value })
            .Distinct().ToList();
        Assert.True(keys.Count >= 10, $"只抓到 {keys.Count} 個代碼，抓法可能壞了");
        foreach (var lang in new[] { "zh-TW", "en-US" })
        {
            var resx = File.ReadAllText(Path.Combine(root, "Resources", "Modules", "ACT", "Views", "MonthEnd", $"Index.{lang}.resx"));
            var missing = keys.Where(k => !resx.Contains($"name=\"{k}\"")).ToList();
            Assert.True(missing.Count == 0, $"{lang} 缺：{string.Join("、", missing)}");
        }
    }
}
