using System.Text.RegularExpressions;
using System.Xml.Linq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Tests.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 手機 PWA 字典（<c>wwwroot/mobile/js/i18n.js</c>＋<c>i18n.modules.js</c>）的守門。
    ///
    /// <para><b>漏鍵是靜默的：</b><c>t()</c> 查不到就原樣回傳鍵名，於是畫面上出現
    /// 「machine.event.EO」這種字串給現場人員看，沒有任何錯誤、也沒有任何日誌。
    /// 2026-08-10 補手冊截圖時就是這樣才發現機台狀態頁少了 EO／GO／CO 三個事件。</para>
    ///
    /// <para>字典是 JS 物件字面值，這裡用 regex 讀而不是解析 JS——
    /// 需要的只是「有沒有這個鍵」，不需要值。</para>
    /// </summary>
    public class MobileI18nKeyTests
    {
        private static readonly Regex KeyPattern =
            new(@"'([a-zA-Z][\w.]*)'\s*:", RegexOptions.Compiled);

        /// <summary>
        /// 字典＝i18n.js（與 Core 一致：引擎＋登入／首頁／共用字串）＋ i18n.modules.js（ProjectZ 模組字串，
        /// 以 registerI18n() 併入）。兩個檔都是 zh-TW 區塊在前、en-US 在後。
        /// </summary>
        private static readonly string[] DictFiles = { "i18n.js", "i18n.modules.js" };

        /// <summary>依語系切出每個字典檔的兩段文字。</summary>
        private static IEnumerable<(string Zh, string En)> DictSegments()
        {
            foreach (var name in DictFiles)
            {
                var text = File.ReadAllText(Path.Combine(ManualPaths.RepoRoot(), "wwwroot", "mobile", "js", name));
                var zhStart = text.IndexOf("'zh-TW':", StringComparison.Ordinal);
                var enStart = text.IndexOf("'en-US':", StringComparison.Ordinal);
                Assert.True(zhStart >= 0 && enStart > zhStart,
                    $"{name} 的字典結構變了（找不到 'zh-TW' / 'en-US' 兩段），本測試需要同步調整。");
                yield return (text[zhStart..enStart], text[enStart..]);
            }
        }

        /// <summary>把字典切成 zh-TW／en-US 兩段各自的鍵集合（兩個檔合併）。</summary>
        private static (HashSet<string> Zh, HashSet<string> En) LoadKeys()
        {
            HashSet<string> KeysIn(string segment) =>
                KeyPattern.Matches(segment).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            var zh = new HashSet<string>(StringComparer.Ordinal);
            var en = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (z, e) in DictSegments()) { zh.UnionWith(KeysIn(z)); en.UnionWith(KeysIn(e)); }

            // 健全性：regex 若失效（引號風格改了、字典改用別的寫法），兩邊會同時抓到 0 個鍵，
            // 而「0 == 0」會讓對稱測試假綠。字典目前約 375 鍵，門檻取保守值。
            Assert.True(zh.Count > 100 && en.Count > 100,
                $"從 i18n.js 只解析到 zh {zh.Count} / en {en.Count} 個鍵，解析方式已失效，本測試形同虛設。");

            return (zh, en);
        }

        [Fact]
        public void 每個機台事件代碼都有中英字典鍵()
        {
            var (zh, en) = LoadKeys();

            // 權威來源是 enum 而不是字典自己——新增事件時漏補翻譯，這裡就會紅。
            var missing = Enum.GetNames<MachineEventType>()
                .SelectMany(code => new[]
                {
                    zh.Contains($"machine.event.{code}") ? null : $"zh-TW 缺 machine.event.{code}",
                    en.Contains($"machine.event.{code}") ? null : $"en-US 缺 machine.event.{code}",
                })
                .Where(m => m != null)
                .ToList();

            Assert.True(missing.Count == 0,
                "手機字典缺少機台事件代碼，畫面會直接顯示原始鍵名（如 machine.event.EO）：\n"
                + string.Join("\n", missing));
        }

        // ── 與桌機用語一致（2026-08-10 決策：一律以桌機 resx 為準）────────────

        private static readonly Regex EntryPattern =
            new(@"'([a-zA-Z][\w.]*)'\s*:\s*'((?:[^'\\]|\\.)*)'", RegexOptions.Compiled);

        /// <summary>i18n.js 的「鍵 → 值」，依語系分段。</summary>
        private static Dictionary<string, string> LoadEntries(bool zh)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (z, e) in DictSegments())
                foreach (System.Text.RegularExpressions.Match m in EntryPattern.Matches(zh ? z : e))
                    result[m.Groups[1].Value] = m.Groups[2].Value;   // 後載入者覆蓋，與 registerI18n 相同
            return result;
        }

        /// <summary>桌機 resx 的「鍵 → 值」。這是全站用語的權威來源。</summary>
        private static Dictionary<string, string> LoadResx(bool zh)
        {
            var file = Path.Combine(ManualPaths.RepoRoot(), "Resources",
                zh ? "ProjectZSharedResource.zh-TW.resx" : "ProjectZSharedResource.en-US.resx");

            return XDocument.Load(file).Root!.Elements("data")
                .Where(d => d.Attribute("name") != null && d.Element("value") != null)
                .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value,
                    StringComparer.Ordinal);
        }

        public static TheoryData<bool> Langs() => new() { true, false };

        [Theory]
        [MemberData(nameof(Langs))]
        public void 機台事件與狀態的用語與桌機一致(bool zh)
        {
            var dict = LoadEntries(zh);
            var resx = LoadResx(zh);

            // (手機字典鍵, resx 鍵) 的對應。狀態的 i18n 鍵是 enum 名的小寫。
            var pairs = Enum.GetNames<MachineEventType>()
                .Select(c => ($"machine.event.{c}", $"MachineEvent{c}"))
                .Concat(Enum.GetNames<MachineStatus>()
                    .Select(s => ($"machine.status.{s.ToLowerInvariant()}", $"MachineStatus{s}")));

            var mismatched = new List<string>();
            var compared = 0;
            foreach (var (mobileKey, resxKey) in pairs)
            {
                if (!resx.TryGetValue(resxKey, out var expected)) continue;   // resx 沒定義就不強求
                compared++;
                if (!dict.TryGetValue(mobileKey, out var actual))
                {
                    mismatched.Add($"{mobileKey}：手機字典沒有這個鍵（桌機為「{expected}」）");
                    continue;
                }
                if (actual != expected)
                    mismatched.Add($"{mobileKey}：手機「{actual}」 vs 桌機 {resxKey}「{expected}」");
            }

            // 健全性：resx 鍵名慣例若變了（或檔案換位置），上面會一路 continue 而全綠。
            // 目前是 11 個事件 + 8 個狀態，門檻取保守值。
            Assert.True(compared >= 15,
                $"只比對到 {compared} 組用語，resx 的鍵名慣例可能已改變，本測試形同虛設。");

            Assert.True(mismatched.Count == 0,
                $"手機與桌機的機台用語不一致（{(zh ? "zh-TW" : "en-US")}）。"
                + "同一台機台在兩邊顯示不同的字，現場與辦公室會對不上話：\n"
                + string.Join("\n", mismatched));
        }

        [Fact]
        public void 每個機台狀態都有對應的字典鍵()
        {
            // 缺鍵的後果不只是顯示原始鍵名——machine-status.page.js 的 STATUS_META
            // 曾把 ExternalRepair／Modification／Retired 全指向 available，
            // 於是廢棄的機台顯示「正常」。那是錯誤資訊，不是漏翻譯。
            var (zh, en) = LoadKeys();

            var missing = Enum.GetNames<MachineStatus>()
                .SelectMany(s => new[]
                {
                    zh.Contains($"machine.status.{s.ToLowerInvariant()}") ? null : $"zh-TW 缺 machine.status.{s.ToLowerInvariant()}",
                    en.Contains($"machine.status.{s.ToLowerInvariant()}") ? null : $"en-US 缺 machine.status.{s.ToLowerInvariant()}",
                })
                .Where(m => m != null)
                .ToList();

            Assert.True(missing.Count == 0,
                "手機字典缺少機台狀態：\n" + string.Join("\n", missing));
        }

        [Fact]
        public void 狀態對應表沒有把不同狀態指到同一個字串()
        {
            // STATUS_META 的值錯指（而不是缺）是靜默的：畫面照樣顯示一個合理的中文詞，
            // 只是那個詞是別的狀態。這裡直接掃 page.js 的對應表，確認每個狀態各自對到
            // 自己的鍵，不倚賴人工比對。
            var js = File.ReadAllText(Path.Combine(
                ManualPaths.RepoRoot(), "wwwroot", "mobile", "mfg", "machine-status.page.js"));

            var table = Regex.Match(js, @"const\s+STATUS_META\s*=\s*\{(.*?)\n\s*\};",
                RegexOptions.Singleline);
            Assert.True(table.Success, "找不到 STATUS_META 對應表，本測試需要同步調整。");

            var wrong = Regex.Matches(table.Groups[1].Value,
                    @"(\w+)\s*:\s*\{\s*key:\s*'machine\.status\.(\w+)'")
                .Select(m => (Status: m.Groups[1].Value, Key: m.Groups[2].Value))
                .Where(x => !string.Equals(x.Status, x.Key, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Status} → machine.status.{x.Key}")
                .ToList();

            Assert.True(wrong.Count == 0,
                "STATUS_META 有狀態指向別的狀態的字典鍵，畫面會顯示錯誤的機台狀態：\n"
                + string.Join("\n", wrong));
        }

        [Fact]
        public void 中英字典的鍵完全對稱()
        {
            var (zh, en) = LoadKeys();

            var onlyZh = zh.Except(en).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var onlyEn = en.Except(zh).OrderBy(k => k, StringComparer.Ordinal).ToList();

            // 只有一邊有＝那個語系的使用者會看到原始鍵名。兩個方向都要驗：
            // 補英文時漏補中文一樣是缺陷，只是比較少人測得到。
            Assert.True(onlyZh.Count == 0 && onlyEn.Count == 0,
                "手機字典中英不對稱：\n"
                + (onlyZh.Count > 0 ? "只有 zh-TW 有：" + string.Join(", ", onlyZh) + "\n" : "")
                + (onlyEn.Count > 0 ? "只有 en-US 有：" + string.Join(", ", onlyEn) : ""));
        }

        // ── 涵蓋率：畫面上的字有沒有真的接到字典 ────────────────────────────
        //
        // 前面幾條驗的是「字典本身完不完整」。但字典完整跟畫面會不會顯示中文是兩件事——
        // 2026-08 補手冊截圖時發現 mfg/sessions.html 與 mfg/reports.html **整頁沒有任何
        // data-i18n**，而 session.* / report.* 幾乎整組鍵早就翻好躺在字典裡沒人用。
        // 症狀不是「沒翻譯」而是「翻好了沒接上」，切成英文照樣整頁中文，且毫無錯誤訊號。

        private static IEnumerable<string> MobilePages() =>
            Directory.EnumerateFiles(Path.Combine(ManualPaths.RepoRoot(), "wwwroot", "mobile"),
                                     "*.html", SearchOption.AllDirectories);

        /// <summary>手機端的頁面腳本與共用腳本，不含字典本身。</summary>
        private static IEnumerable<string> MobileScripts() =>
            Directory.EnumerateFiles(Path.Combine(ManualPaths.RepoRoot(), "wwwroot", "mobile"),
                                     "*.js", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("i18n.js", StringComparison.Ordinal)
                         && !f.EndsWith("i18n.modules.js", StringComparison.Ordinal)
                         && !f.Replace('\\', '/').Contains("/libs/", StringComparison.Ordinal)
                         // jest 規格檔不會送到手機上，describe/it 的標題與斷言訊息都是開發者文字，
                         // 與已排除的 console.* 同一個道理。不排除的話，用中文描述測試就會把守門測試弄紅。
                         && !f.Replace('\\', '/').Contains("/__tests__/", StringComparison.Ordinal)
                         && !f.EndsWith(".test.js", StringComparison.Ordinal)
                         && !f.EndsWith("sw.js", StringComparison.Ordinal));

        private static readonly Regex Comments =
            new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ConsoleCall =
            new(@"console\.\w+\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Cjk = new(@"[一-鿿]", RegexOptions.Compiled);

        [Fact]
        public void 手機頁面的可見中文都掛在字典上()
        {
            var offenders = new List<string>();

            foreach (var file in MobilePages())
            {
                var name = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);
                var html = File.ReadAllText(file);
                var body = Regex.Match(html, @"<body.*?</body>", RegexOptions.Singleline);
                if (!body.Success) continue;

                // <script> 內是 JS，由下一條測試負責；註解不算畫面文字。
                var text = Regex.Replace(body.Value, @"<script.*?</script>", "", RegexOptions.Singleline);
                text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match m in
                         Regex.Matches(text, @"<(\w+)([^>]*)>([^<]*)"))
                {
                    var attrs = m.Groups[2].Value;
                    var inner = m.Groups[3].Value.Trim();

                    if (inner.Length > 0 && Cjk.IsMatch(inner) && !attrs.Contains("data-i18n"))
                        offenders.Add($"{name}：<{m.Groups[1].Value}> 「{Trim40(inner)}」");

                    foreach (var attr in new[] { "placeholder", "title" })
                    {
                        var v = Regex.Match(attrs, $@"\b{attr}=""([^""]*)""");
                        if (v.Success && Cjk.IsMatch(v.Groups[1].Value)
                            && !attrs.Contains($"data-i18n-{attr}"))
                            offenders.Add($"{name}：@{attr}=「{Trim40(v.Groups[1].Value)}」");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "以下手機畫面文字沒有 data-i18n，切成英文仍會顯示中文：\n"
                + string.Join("\n", offenders.Take(30))
                + (offenders.Count > 30 ? $"\n…共 {offenders.Count} 處" : ""));
        }

        [Fact]
        public void 手機腳本不得有面向使用者的中文字面()
        {
            // console.* 是開發者訊息，不面向現場，排除。
            var offenders = new List<string>();

            foreach (var file in MobileScripts())
            {
                var name = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);
                var src = ConsoleCall.Replace(Comments.Replace(File.ReadAllText(file), ""), "");

                foreach (System.Text.RegularExpressions.Match m in
                         Regex.Matches(src, @"'([^'\n]*)'|""([^""\n]*)""|`((?:[^`\\]|\\.)*)`",
                                       RegexOptions.Singleline))
                {
                    var lit = new[] { m.Groups[1], m.Groups[2], m.Groups[3] }
                        .FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value;
                    if (lit != null && Cjk.IsMatch(lit))
                        offenders.Add($"{name}：「{Trim40(lit.Trim())}」");
                }
            }

            Assert.True(offenders.Count == 0,
                "以下手機腳本直接寫死中文，切成英文仍會顯示中文（請改走 t() / tf()）：\n"
                + string.Join("\n", offenders.Take(30))
                + (offenders.Count > 30 ? $"\n…共 {offenders.Count} 處" : ""));
        }

        [Fact]
        public void 頁面用到的字典鍵全部存在()
        {
            // 缺鍵不會拋錯：t() 查不到就原樣回傳鍵名，畫面直接顯示 "ipqc_proc.op_seq"。
            // 本次補 i18n 時就打錯過一個鍵，靠這條抓出來。
            var (zh, en) = LoadKeys();
            var missing = new List<string>();

            void Check(string where, string key)
            {
                // 動態組出來的鍵（'machine.event.' + code）驗不了，跳過。
                if (key.EndsWith('.')) return;
                if (!zh.Contains(key)) missing.Add($"{where}：zh-TW 缺 {key}");
                if (!en.Contains(key)) missing.Add($"{where}：en-US 缺 {key}");
            }

            foreach (var file in MobilePages())
            {
                var name = Path.GetFileName(file);
                foreach (System.Text.RegularExpressions.Match m in
                         Regex.Matches(File.ReadAllText(file), @"data-i18n(?:-\w+)?=""([^""]+)"""))
                    Check(name, m.Groups[1].Value);
            }

            foreach (var file in MobileScripts())
            {
                var name = Path.GetFileName(file);
                var src = Comments.Replace(File.ReadAllText(file), "");
                foreach (System.Text.RegularExpressions.Match m in
                         Regex.Matches(src, @"\btf?\('([\w.]+)'"))
                    Check(name, m.Groups[1].Value);
            }

            Assert.True(missing.Count == 0,
                "以下鍵有人用但字典裡沒有，畫面會顯示原始鍵名：\n" + string.Join("\n", missing));
        }

        private static string Trim40(string s) => s.Length <= 40 ? s : s[..40] + "…";
    }
}
