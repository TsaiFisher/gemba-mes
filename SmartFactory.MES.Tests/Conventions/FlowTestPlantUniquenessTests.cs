using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions;

/// <summary>
/// 流程測試（<c>IntegrationTests/Flows</c>）的廠別代號必須彼此互斥。
///
/// <para><b>為什麼要守</b>：整合測試共用一個資料庫，<c>FlowTestBase</c> 的清理與斷言
/// 全部以 <c>Plant</c> 為界。兩支測試選到同一個代號時**不會有人報錯**——它們會互相
/// 刪對方的資料、互相看到對方的單據，而**紅的通常是先跑的那一支，不是後加的那一支**。
/// 2026-09-08 實際踩到：新加的 <c>MfgSimplifiedCostFlowTests</c> 選了 <c>ZZMFGS</c>，
/// 而那是 <c>MfgMaterialSlipFlowTests</c> 的；症狀是後者拋
/// <c>FK_ProductionSessions_Dispatches_DispatchId</c> 外鍵違反，訊息裡完全沒有新測試的名字。</para>
///
/// <para>掃原始碼而非反射：<c>Plant</c> 是 <c>protected abstract</c>，要拿到值得先實例化，
/// 而實例化流程測試就會連資料庫。這一條要在**單元測試**裡擋得住才有意義。</para>
/// </summary>
public class FlowTestPlantUniquenessTests
{
    [Fact]
    public void EveryFlowTest_UsesItsOwnPlantCode()
    {
        var dir = Path.Combine(ConventionScan.RepoRoot(), "SmartFactory.MES.IntegrationTests", "Flows");
        Assert.True(Directory.Exists(dir), $"找不到流程測試目錄：{dir}");

        var rx = new Regex(@"string\s+Plant\s*=>\s*""(?<code>[^""]+)""", RegexOptions.Compiled);
        var byCode = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(file)))
                (byCode.TryGetValue(m.Groups["code"].Value, out var l)
                    ? l
                    : byCode[m.Groups["code"].Value] = new List<string>())
                        .Add(Path.GetFileName(file));

        // 下限：抓不到任何代號時上面的迴圈也不會紅，等於這條規則被靜靜關掉
        Assert.True(byCode.Count >= 25,
            $"只掃到 {byCode.Count} 個廠別代號，遠少於現有流程測試數——正則可能已與寫法脫節");

        var dupes = byCode.Where(kv => kv.Value.Count > 1)
                          .Select(kv => $"{kv.Key} ← {string.Join("、", kv.Value)}")
                          .ToList();
        Assert.True(dupes.Count == 0,
            "流程測試的廠別代號重複，兩支會共用同一批資料而互相破壞：\n  " + string.Join("\n  ", dupes));
    }
}
