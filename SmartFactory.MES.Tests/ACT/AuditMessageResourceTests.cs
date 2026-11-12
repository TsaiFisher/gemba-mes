using System.Reflection;
using System.Text.RegularExpressions;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 稽核訊息鍵的完整性守門。
///
/// <para>這批鍵的失效方式**不會被任何既有測試看到**：翻譯漏補時
/// <c>IStringLocalizer</c> 不報錯，只是回傳鍵名；而稽核明細是背景排程產生的，
/// 沒有人會為了看一句描述而去跑月結。所以三份 resx 的完整性必須靜態守住。</para>
/// </summary>
public class AuditMessageResourceTests
{
    private static readonly string[] Keys = typeof(AuditMessages)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    private static string Resx(string suffix) => Path.Combine(
        ConventionScan.RepoRoot(), "Resources", $"ProjectZAuditResource{suffix}.resx");

    /// <summary>模板裡最大的 {n}＋1＝需要的參數個數。</summary>
    private static int ArgCount(string template)
    {
        var idx = Regex.Matches(template, @"\{(\d+)(?::[^}]*)?\}")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return idx.Count == 0 ? 0 : idx.Max() + 1;
    }

    [Fact]
    public void 常數不是空的()
        => Assert.True(Keys.Length >= 30, $"只掃到 {Keys.Length} 個鍵，反射條件可能壞了");

    [Fact]
    public void 每個鍵在三份resx都有值()
    {
        var neutral = ConventionScan.ResxKeys(Resx(""));
        var zh      = ConventionScan.ResxKeys(Resx(".zh-TW"));
        var en      = ConventionScan.ResxKeys(Resx(".en-US"));

        var missing = Keys
            .Where(k => !neutral.Contains(k) || !zh.Contains(k) || !en.Contains(k))
            .ToList();

        Assert.True(missing.Count == 0,
            "下列鍵缺 resx（中性/zh-TW/en-US 三份都要有）：" + string.Join("、", missing));
    }

    [Fact]
    public void 中性資源在執行期取得到()
    {
        // 與上一支不同：這支驗的是**資源有沒有被編進組件、資訊清單名對不對**。
        // resx 檔在、但 csproj 沒收進去或根命名空間不同時，上一支照樣綠，
        // 而背景排程會把鍵名當描述存進資料庫。
        var missing = Keys.Where(k => !AuditMessageTemplates.HasKey(k)).ToList();
        Assert.True(missing.Count == 0, "中性資源取不到：" + string.Join("、", missing));
    }

    [Fact]
    public void 中英模板的參數個數一致()
    {
        var zh = ConventionScan.ResxValues(Resx(".zh-TW"));
        var en = ConventionScan.ResxValues(Resx(".en-US"));

        var mismatched = Keys
            .Where(k => zh.ContainsKey(k) && en.ContainsKey(k))
            .Where(k => ArgCount(zh[k]) != ArgCount(en[k]))
            .Select(k => $"{k}（zh {ArgCount(zh[k])} / en {ArgCount(en[k])}）")
            .ToList();

        // 個數不一致的後果不是排版難看，是英文少印一個數字而完全不會報錯——
        // 與 _Pagination 當初 PageInfo 兩種格式並存是同一種失效。
        Assert.True(mismatched.Count == 0, "中英參數個數不一致：" + string.Join("、", mismatched));
    }

    [Fact]
    public void 中性與zhTW內容相同()
    {
        var neutral = ConventionScan.ResxValues(Resx(""));
        var zh      = ConventionScan.ResxValues(Resx(".zh-TW"));
        var drifted = Keys
            .Where(k => neutral.ContainsKey(k) && zh.ContainsKey(k) && neutral[k] != zh[k])
            .ToList();
        // 兩份分開存是為了讓 invariant 語系也取得到，不是為了讓它們各自演化。
        Assert.True(drifted.Count == 0, "中性與 zh-TW 已漂移：" + string.Join("、", drifted));
    }

    [Fact]
    public void 沒有用不到的鍵()
    {
        var src = Directory
            .EnumerateFiles(Path.Combine(ConventionScan.RepoRoot(), "Modules"), "*.cs",
                            SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("AuditMessages.cs"))
            .Select(File.ReadAllText)
            .Aggregate(new System.Text.StringBuilder(), (sb, t) => sb.Append(t))
            .ToString();

        var unused = Keys.Where(k => !src.Contains($"AuditMessages.{k}")).ToList();
        // 用不到的鍵本身無害，但它代表「某個產生點被改掉卻沒人動 resx」——
        // 那通常伴隨另一個方向的錯：新的產生點用了不存在的鍵。
        Assert.True(unused.Count == 0, "宣告了但沒有任何產生點使用：" + string.Join("、", unused));
    }
}
