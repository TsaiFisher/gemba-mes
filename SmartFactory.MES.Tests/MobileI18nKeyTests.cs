using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;   // 部署專案的 GlobalUsings 含 Moq（Moq.Match）

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 手機頁用到的 i18n 鍵（HTML 的 data-i18n*、JS 的 t('…')／tf('…')）必須在字典的兩個語系都有。
    ///
    /// <para>漏鍵不會報錯：applyI18n() 查無鍵時保留 HTML 原文（中文），t() 則回傳鍵名本身——
    /// 英文介面上看到的是中文或 "login.err_empty" 這種原始鍵，沒有任何測試會紅。</para>
    ///
    /// <para>字典＝Core 的 <c>wwwroot/mobile/js/i18n.js</c>，加上部署專案以 registerI18n() 併入的
    /// <c>i18n.modules.js</c>（存在才讀）。</para>
    /// </summary>
    public class MobileI18nKeyTests
    {
        private static readonly Regex Entry = new(@"'([a-z_0-9]+\.[^']+)'\s*:", RegexOptions.Compiled);
        private static readonly Regex HtmlKey = new(@"\bdata-i18n(?:-[a-z]+)?=""([^""]+)""", RegexOptions.Compiled);
        // 結尾不得是「.」：t('dispatch.status.' + code) 是動態組鍵的前綴，不是一個鍵
        private static readonly Regex JsKey = new(@"\btf?\(\s*'([a-z_0-9]+\.[a-zA-Z_0-9.]*[a-zA-Z_0-9])'\s*[,)]", RegexOptions.Compiled);
        private static readonly Regex JsComment = new(@"/\*.*?\*/|(?<![:'""\\])//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

        private static string MobileRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "SmartFactory.MES.sln")))
                dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? throw new InvalidOperationException("找不到 Repo 根目錄"), "wwwroot", "mobile");
        }

        /// <summary>依語系收集字典鍵：以 'en-US': { 為界切成兩段（兩個檔都是 zh-TW 在前）。</summary>
        private static (HashSet<string> Zh, HashSet<string> En) Dictionary(string mobile)
        {
            var zh = new HashSet<string>(); var en = new HashSet<string>();
            foreach (var name in new[] { "i18n.js", "i18n.modules.js" })
            {
                var path = Path.Combine(mobile, "js", name);
                if (!File.Exists(path)) continue;
                var src = File.ReadAllText(path);
                var split = src.IndexOf("'en-US': {", StringComparison.Ordinal);
                Assert.True(split > 0, $"{name} 找不到 'en-US' 區塊");
                foreach (Match m in Entry.Matches(src[..split])) zh.Add(m.Groups[1].Value);
                foreach (Match m in Entry.Matches(src[split..])) en.Add(m.Groups[1].Value);
            }
            return (zh, en);
        }

        [Fact]
        public void EveryKeyUsedByMobilePages_ExistsInBothLanguages()
        {
            var mobile = MobileRoot();
            var (zh, en) = Dictionary(mobile);
            Assert.True(zh.Count >= 30, $"字典只讀到 {zh.Count} 個鍵——解析可能已失效");

            var used = new SortedSet<string>();
            foreach (var f in Directory.EnumerateFiles(mobile, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains("__tests__") || f.Contains(Path.DirectorySeparatorChar + "libs" + Path.DirectorySeparatorChar)) continue;
                var name = Path.GetFileName(f);
                if (name.StartsWith("i18n")) continue;
                var src = File.ReadAllText(f);
                if (f.EndsWith(".js")) src = JsComment.Replace(src, "");   // 註解裡的範例鍵不算
                var rx = f.EndsWith(".html") ? HtmlKey : f.EndsWith(".js") ? JsKey : null;
                if (rx == null) continue;
                foreach (Match m in rx.Matches(src)) used.Add(m.Groups[1].Value);
            }
            Assert.NotEmpty(used);

            var missing = used.Where(k => !zh.Contains(k) || !en.Contains(k))
                              .Select(k => $"{k}（{(zh.Contains(k) ? "" : "缺 zh-TW ")}{(en.Contains(k) ? "" : "缺 en-US")}）").ToList();
            Assert.True(missing.Count == 0, "手機頁用到但字典沒有的鍵：\n" + string.Join("\n", missing));
        }
    }
}
