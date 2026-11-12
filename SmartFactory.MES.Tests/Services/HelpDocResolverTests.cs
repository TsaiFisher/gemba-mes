using SmartFactory.MES.Services;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// HelpDocResolver（畫面「?」說明連結 → 使用手冊頁）對應守門：
    /// 1. kebab-case 轉換正確（含連續大寫縮寫）。
    /// 2. 每個登錄的 controller 都解析得到實際存在的手冊頁檔案，
    ///    防止新增畫面時只登錄對應卻漏建手冊頁（或反之改名漏同步）。
    /// 3. UI 語系為英文時連結帶 ?lang=en。
    /// 4. 手冊頁本身維持中英雙語對稱（見 UserManualBilingualTests）。
    /// </summary>
    public class HelpDocResolverTests
    {
        [Theory]
        [InlineData("Product", "/docs/user/bas/product.html")]
        [InlineData("DocNumberConfig", "/docs/user/bas/doc-number-config.html")]
        [InlineData("APInvoice", "/docs/user/fin/ap-invoice.html")]
        [InlineData("ARAging", "/docs/user/fin/ar-aging.html")]
        [InlineData("Oee", "/docs/user/mfg/oee.html")]
        [InlineData("MrpRun", "/docs/user/mfg/mrp-run.html")]
        [InlineData("SpcAnalysis", "/docs/user/qcr/spc-analysis.html")]
        [InlineData("CrossPlantTransfer", "/docs/user/inv/cross-plant-transfer.html")]
        public void Resolve_KnownControllers_ReturnsExpectedUrl(string controller, string expected)
        {
            Assert.Equal(expected, HelpDocResolver.Resolve(controller, "zh"));
        }

        [Theory]
        [InlineData("Product", "/docs/user/bas/product.html?lang=en")]
        [InlineData("APInvoice", "/docs/user/fin/ap-invoice.html?lang=en")]
        public void Resolve_EnglishUi_AppendsLangParameter(string controller, string expected)
        {
            Assert.Equal(expected, HelpDocResolver.Resolve(controller, "en"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("NotARegisteredController")]
        public void Resolve_UnknownController_ReturnsNull(string? controller)
        {
            Assert.Null(HelpDocResolver.Resolve(controller));
        }

        [Fact]
        public void WithLang_ZhIsDefault_NoParameterAdded()
        {
            Assert.Equal("/docs/user/index.html",
                HelpDocResolver.WithLang("/docs/user/index.html", "zh"));
            Assert.Equal("/docs/user/index.html?lang=en",
                HelpDocResolver.WithLang("/docs/user/index.html", "en"));
        }

        [Fact]
        public void Resolve_EveryRegisteredController_MapsToExistingManualPage()
        {
            var root = ManualPaths.RepoRoot();
            var mapField = typeof(HelpDocResolver).GetField(
                "ControllerModule", BindingFlags.NonPublic | BindingFlags.Static)!;
            var map = (Dictionary<string, string>)mapField.GetValue(null)!;

            Assert.NotEmpty(map);
            var missing = new List<string>();
            foreach (var controller in map.Keys)
            {
                var url = HelpDocResolver.Resolve(controller, "zh")!;
                // url 形如 /docs/...，實體檔在 wwwroot/docs/...
                var file = Path.Combine(root, "wwwroot" + url.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                    missing.Add($"{controller} -> {url}");
            }

            Assert.True(missing.Count == 0,
                "以下畫面已登錄 HelpDocResolver 但找不到手冊頁：\n" + string.Join("\n", missing));
        }
    }

    /// <summary>
    /// 使用手冊中英雙語守門。手冊頁的顯示語言純由 CSS（html[data-doc-lang]）決定，
    /// 每段文字以相鄰的 &lt;span lang="zh"&gt;/&lt;span lang="en"&gt; 成對存在；
    /// 新增或修改手冊頁時只補中文會讓英文版出現空白段落，故在此守門。
    /// </summary>
    public class UserManualBilingualTests
    {
        private static readonly Regex ZhSpan = new(@"<span lang=""zh"">", RegexOptions.Compiled);
        private static readonly Regex EnSpan = new(@"<span lang=""en"">", RegexOptions.Compiled);
        private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex LangSpanPair =
            new(@"<span lang=""(?:zh|en)"">.*?</span>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex TextNode = new(@">([^<>]*)<", RegexOptions.Compiled);
        private static readonly Regex Cjk = new(@"[一-鿿]", RegexOptions.Compiled);

        public static TheoryData<string> Pages()
        {
            var data = new TheoryData<string>();
            foreach (var f in ManualPaths.AllPages())
                data.Add(Path.GetRelativePath(ManualPaths.UserDocsRoot(), f));
            return data;
        }

        [Fact]
        public void ManualHasPages()
        {
            Assert.NotEmpty(ManualPaths.AllPages());
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void EveryPage_IsBilingual(string relativePath)
        {
            var text = File.ReadAllText(Path.Combine(ManualPaths.UserDocsRoot(), relativePath));
            var body = Comment.Replace(text, string.Empty);

            Assert.True(ZhSpan.Count(body) == EnSpan.Count(body),
                $"{relativePath}：中英節點數不對稱（zh {ZhSpan.Count(body)} / en {EnSpan.Count(body)}）。" +
                "每段文字都必須同時提供 <span lang=\"zh\"> 與 <span lang=\"en\">。");

            Assert.Contains("doc-i18n.css", body);
            Assert.Contains("doc-i18n.js", body);
            Assert.Contains("data-doc-lang=\"zh\"", body);
            Assert.Contains("id=\"docLangToggle\"", body);
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void EveryPage_HasNoUnwrappedChineseText(string relativePath)
        {
            var text = File.ReadAllText(Path.Combine(ManualPaths.UserDocsRoot(), relativePath));
            var body = Comment.Replace(text, string.Empty);
            body = LangSpanPair.Replace(body, string.Empty);
            body = Regex.Replace(body, @"<title>[^<]*</title>", string.Empty);

            var stray = TextNode.Matches(body)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(t => t.Length > 0 && Cjk.IsMatch(t))
                .Distinct()
                .ToList();

            Assert.True(stray.Count == 0,
                $"{relativePath}：以下中文未包在 <span lang=\"zh\"> 內，英文模式會直接漏出中文：\n" +
                string.Join("\n", stray.Take(10)));
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void EveryPage_TitleAndScreenshotsHaveEnglishCounterpart(string relativePath)
        {
            var text = File.ReadAllText(Path.Combine(ManualPaths.UserDocsRoot(), relativePath));

            var title = Regex.Match(text, @"<title>([^<]*)</title>");
            if (title.Success && Cjk.IsMatch(title.Groups[1].Value))
            {
                Assert.True(text.Contains("data-title-en=\""),
                    $"{relativePath}：<title> 為中文但 <html> 缺 data-title-en，英文模式分頁標題會是中文。");
            }

            foreach (System.Text.RegularExpressions.Match img in Regex.Matches(text, @"<img\b[^>]*>"))
            {
                var tag = img.Value;
                if (!tag.Contains("src=\"img/")) continue;
                Assert.True(tag.Contains("data-src-en=\"img/en/"),
                    $"{relativePath}：截圖缺 data-src-en，英文模式會顯示中文介面截圖。\n{tag}");
                if (Regex.IsMatch(tag, @"alt=""[^""]*[一-鿿]"))
                {
                    Assert.True(tag.Contains("data-alt-en=\""),
                        $"{relativePath}：截圖 alt 為中文但缺 data-alt-en。\n{tag}");
                }
            }
        }
    }

    internal static class ManualPaths
    {
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null &&
                   !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        internal static string UserDocsRoot() =>
            Path.Combine(RepoRoot(), "wwwroot", "docs", "user");

        internal static IReadOnlyList<string> AllPages() =>
            Directory.GetFiles(UserDocsRoot(), "*.html", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal)
                     .ToList();
    }
}
