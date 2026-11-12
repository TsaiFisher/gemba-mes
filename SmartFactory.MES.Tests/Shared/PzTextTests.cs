using System.Globalization;
using System.Text.RegularExpressions;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Tests.Shared;

/// <summary>
/// PzText：模組服務層訊息取自 ProjectZMessageResource，依 UI 文化切換。
/// 漏登錄的鍵在畫面上會直接顯示鍵名（不丟例外、不會被任何測試以外的機制發現），故逐鍵檢查兩語系。
/// </summary>
public class PzTextTests
{
    private static T WithCulture<T>(string name, Func<T> f)
    {
        var saved = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentUICulture = new CultureInfo(name); return f(); }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    [Fact]
    public void Get_FollowsUiCulture_AndFormatsArgs()
    {
        Assert.Equal("找不到品號 P1", WithCulture("zh-TW", () => PzText.Get("Inv_ProductNotFoundFmt", "P1")));
        Assert.Equal("Product P1 not found.", WithCulture("en-US", () => PzText.Get("Inv_ProductNotFoundFmt", "P1")));
        Assert.Equal("找不到品號 P1", WithCulture("", () => PzText.Get("Inv_ProductNotFoundFmt", "P1")));
    }

    /// <summary>查無鍵回傳鍵名而非丟 MissingManifestResourceException（沒有中性 .resx）。</summary>
    [Theory]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Get_UnknownKey_ReturnsKey(string culture)
        => Assert.Equal("NoSuchKey_X", WithCulture(culture, () => PzText.Get("NoSuchKey_X")));

    [Fact]
    public void EveryKeyUsedInModules_ExistsInBothCultures_WithSamePlaceholders()
    {
        var root = FindRepoRoot();
        var used = Directory.EnumerateFiles(Path.Combine(root, "Modules"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"PzText\.Get\(""(\w+)""").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);

        var dicts = new[] { "zh-TW", "en-US" }.ToDictionary(c => c, c =>
            Regex.Matches(File.ReadAllText(Path.Combine(root, "Resources", $"ProjectZMessageResource.{c}.resx")),
                    @"<data name=""(\w+)""[^>]*>\s*<value>(.*?)</value>", RegexOptions.Singleline)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value));

        var problems = new List<string>();
        foreach (var k in used)
        {
            if (!dicts["zh-TW"].TryGetValue(k, out var zh)) { problems.Add($"zh-TW 缺 {k}"); continue; }
            if (!dicts["en-US"].TryGetValue(k, out var en)) { problems.Add($"en-US 缺 {k}"); continue; }
            static string Ph(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
            if (Ph(zh) != Ph(en)) problems.Add($"{k} 佔位符不一致：zh[{Ph(zh)}] en[{Ph(en)}]");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 SmartFactory.MES.csproj");
    }
}
