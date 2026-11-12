using System.Globalization;
using System.Text.RegularExpressions;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests
{
    /// <summary>CoreText：Controller 訊息取自 SharedResource，依 UI 文化切換；漏登錄的鍵在畫面上會直接顯示鍵名。</summary>
    public class CoreTextTests
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
            Assert.Equal("廠別新增成功", WithCulture("zh-TW", () => CoreText.Get("Msg_PlantCreated")));
            Assert.Equal("Plant added.", WithCulture("en-US", () => CoreText.Get("Msg_PlantCreated")));
            Assert.Equal("使用者（3 位）", WithCulture("zh-TW", () => CoreText.Get("Msg_RoleInUse_UsersFmt", 3)));
            // 非支援文化（測試執行緒常見的 Invariant）落在預設 zh-TW
            Assert.Equal("廠別新增成功", WithCulture("", () => CoreText.Get("Msg_PlantCreated")));
        }

        [Fact]
        public void EveryKeyUsedInCode_ExistsInBothCultures()
        {
            var root = FindRepoRoot();
            var used = Directory.EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs")
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs"))
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"CoreText\.Get\(""(\w+)""").Select(m => m.Groups[1].Value))
                .Concat(Regex.Matches(File.ReadAllText(Path.Combine(root, "Controllers", "ApprovalController.cs")), @"""(Msg_\w+)""").Select(m => m.Groups[1].Value))
                .ToHashSet();
            Assert.NotEmpty(used);
            foreach (var culture in new[] { "zh-TW", "en-US" })
            {
                var resx = File.ReadAllText(Path.Combine(root, "Resources", $"SharedResource.{culture}.resx"));
                var missing = used.Where(k => !resx.Contains($"<data name=\"{k}\"")).ToList();
                Assert.True(missing.Count == 0, $"{culture} 缺鍵：{string.Join(", ", missing)}");
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("找不到 SmartFactory.MES.csproj");
        }
    }
}

namespace SmartFactory.MES.Tests
{
    /// <summary>CoreText.Token／Render：簽核歷程等寫入資料庫的系統文字依檢視者語系顯示。</summary>
    public class CoreTextTokenTests
    {
        private static string? RenderIn(string culture, string? text)
        {
            var saved = System.Globalization.CultureInfo.CurrentUICulture;
            try { System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture); return SmartFactory.MES.Services.CoreText.Render(text); }
            finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
        }

        [Fact]
        public void Token_RendersPerCulture_WithArgs()
        {
            var t = SmartFactory.MES.Services.CoreText.Token("ApprovalNote_ReassignedFmt", "U2", "人員異動");
            Assert.Equal("重新指派簽核人：U2｜原因：人員異動", RenderIn("zh-TW", t));
            Assert.Equal("Approver reassigned to U2. Reason: 人員異動", RenderIn("en-US", t));
        }

        [Fact]
        public void Render_KeepsPlainText_UnknownKey_AndSanitizesArgs()
        {
            Assert.Equal("同意，請盡快處理", RenderIn("en-US", "同意，請盡快處理"));   // 使用者意見／舊資料
            Assert.Null(RenderIn("en-US", null));
            Assert.Equal("[[NoSuchKey|x]]", RenderIn("en-US", "[[NoSuchKey|x]]"));
            var t = SmartFactory.MES.Services.CoreText.Token("ApprovalNote_DocDeletedFmt", "a|b]c");
            Assert.Equal("Cancelled automatically because the document was deleted. Reason: a/b)c", RenderIn("en-US", t));
        }

        [Fact]
        public void EveryApprovalNoteKey_ExistsInBothCultures()
        {
            var src = File.ReadAllText(Path.Combine(FindRoot(), "Services", "ApprovalService.cs"));
            var keys = System.Text.RegularExpressions.Regex.Matches(src, @"CoreText\.Token\(""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(keys.Count >= 8);
            foreach (var c in new[] { "zh-TW", "en-US" })
            {
                var resx = File.ReadAllText(Path.Combine(FindRoot(), "Resources", $"SharedResource.{c}.resx"));
                Assert.All(keys, k => Assert.Contains($"<data name=\"{k}\"", resx));
            }
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
}

namespace SmartFactory.MES.Tests
{
    public class CoreTextMissingKeyTests
    {
        /// <summary>查無鍵回傳鍵名而非丟例外（原本 ResourceManager 往中性資源找會丟 MissingManifestResourceException，畫面變 500）。</summary>
        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        [InlineData("")]
        public void Get_UnknownKey_ReturnsKey(string culture)
        {
            var saved = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
                Assert.Equal("NoSuchKey_X", SmartFactory.MES.Services.CoreText.Get("NoSuchKey_X"));
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
        }
    }
}
