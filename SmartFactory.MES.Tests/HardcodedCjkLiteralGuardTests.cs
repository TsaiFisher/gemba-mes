using System.Text;
using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;   // 部署專案的 GlobalUsings 含 Moq（Moq.Match）

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// C# 程式碼中含中文的字串常值，必須是「刻意的」：否則英文介面會冒出中文。
    ///
    /// <para><b>為什麼要有這一條</b>：畫面（.cshtml）的雙語檢查只看得到畫面上寫死的字。
    /// 例外訊息、TempData、JSON message、匯出表頭、ModelBinding 訊息全在 C# 裡，
    /// 而且多半只在操作失敗時才出現——切換語系看畫面永遠看不到它們。
    /// 2026-10 一次清出數百則，全是這個盲點。</para>
    ///
    /// <para><b>判定</b>：使用者會看到的文字一律走資源檔（Core：<c>CoreText.Get</c>；
    /// 寫進資料庫日後再顯示的走 <c>CoreText.Token</c>）。以下情境視為刻意、不算違規：</para>
    /// <list type="bullet">
    /// <item>日誌呼叫（<c>LogInformation</c>／<c>LogWarning</c>…）的參數——日誌給維運看，不隨使用者語系。</item>
    /// <item>屬性參數（例：<c>[PermissionGateExempt("…")]</c>）——給開發者看的說明。</item>
    /// <item>同一敘述內有語系判斷（<c>isZh</c>／<c>IsEn</c>／<c>IsEnglish()</c>…）——已寫成雙語分支。</item>
    /// </list>
    /// <para>其餘刻意保留的（授權訊息、稽核紀錄文字、種子資料、開發工具錯誤）登錄在
    /// <c>HardcodedCjkLiteralBaseline.txt</c>，每段註明理由；部署專案自己的例外放
    /// <c>HardcodedCjkLiteralBaseline.Local.txt</c>（Core 不提供此檔，兩邊共用本測試不產生漂移）。
    /// baseline 以「檔案＋字面原文」為鍵，不綁行號；已不存在的條目也會讓測試紅，避免清單只增不減。</para>
    ///
    /// <para><b>畫面與腳本</b>（.cshtml／wwwroot 的 .js 與 .html）同一份 baseline、同一個測試：
    /// .cshtml 逐行判斷（L["…"]、註解、isEn／isZh 雙語三元含跨行不算；*.zh-TW.cshtml 依語系分檔不看）；
    /// .js 看字串常值（所在敘述是 console.* 或語系分支不算）；手機 .html 的文字要有所屬元素的 data-i18n、
    /// placeholder／title 要有 data-i18n-{屬性}（原文只是後備）。兩類共用 baseline，過期檢查才不會互相誤判。</para>
    ///
    /// <para>掃描範圍沿用 <see cref="DisplayTimeZoneGuardTests"/> 的排除式，並斷言掃描量。</para>
    /// </summary>
    public class HardcodedCjkLiteralGuardTests
    {
        private const string BaselineFile = "HardcodedCjkLiteralBaseline.txt";
        private const string LocalBaselineFile = "HardcodedCjkLiteralBaseline.Local.txt";

        private static readonly Regex Cjk = new(@"[\u4e00-\u9fff\u3400-\u4dbf]", RegexOptions.Compiled);
        private static readonly Regex LoggerCall = new(
            @"\b(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)|LoggerMessage)\s*\(|\.Log\s*\(" +
            @"|\bLog\.(?:Verbose|Debug|Information|Warning|Error|Fatal)\s*\(",   // Serilog 靜態 Log
            RegexOptions.Compiled);
        private static readonly Regex LanguageBranch = new(
            @"\b(?:is|Is)(?:Zh|En|Chinese|English)\w*\b", RegexOptions.Compiled);
        // 未配對的 { 之前若是這些，就是物件／集合初始設定式而不是程式區塊，要繼續往前找敘述起點
        private static readonly Regex InitializerBrace = new(
            @"(?:\bnew\b[^;{}]*|=|\(|,|\?|:|=>|\[\])\s*$", RegexOptions.Compiled);

        private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", "wwwroot", "Migrations", "tools", "scripts",
            "SmartFactory.MES.Tests", "SmartFactory.MES.IntegrationTests",
            "SmartFactory.MES.UITests", "SmartFactory.MES.MachineId",
            "SmartFactory.MES.LicenseAuthority", "SmartFactory.MES.Security",
        };

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var segments = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(Skipped.Contains)) continue;
                yield return f;
            }
        }

        private readonly record struct Hit(string Rel, int Line, string Text);

        [Fact]
        public void UserFacingText_IsNotHardcodedChinese()
        {
            var root = RepoRoot();
            var (baseline, wholeFiles, staleCheckable) = LoadBaseline(root);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var offenders = new List<string>();

            var hits = CSharpHits(root, out var csProbed).Concat(ViewAndScriptHits(root, out var viewProbed)).ToList();

            // 撰寫時 Core 93 個 .cs／約 60 個畫面與腳本檔，部署專案各約 770／600；
            // 門檻要抓的是「掃描器整個失效」，不是精確計數
            Assert.True(csProbed >= 80,
                $"C# 掃描器只讀到 {csProbed} 個檔案，遠少於預期——規則可能已經失效，此時它會永遠是綠的。請先修掃描範圍。");
            Assert.True(viewProbed >= 40,
                $"畫面／腳本掃描器只讀到 {viewProbed} 個檔案，遠少於預期——規則可能已經失效。請先修掃描範圍。");

            foreach (var h in hits)
            {
                if (wholeFiles.Contains(h.Rel)) { used.Add(h.Rel + "\t*"); continue; }
                var key = h.Rel + "\t" + Escape(h.Text);
                if (baseline.Contains(key)) { used.Add(key); continue; }
                offenders.Add($"{h.Rel}:{h.Line}  {h.Text}");
            }

            Assert.True(offenders.Count == 0,
                "以下文字含中文，英文介面會原樣顯示：\n" +
                "C#：使用者看得到的訊息請改 CoreText.Get(key)（部署專案用各自的資源類別），寫進資料庫的改 CoreText.Token；\n" +
                "畫面：改 @L[\"key\"]／@SL[\"key\"]；手機 HTML 加 data-i18n／data-i18n-placeholder／data-i18n-title；JS 改走注入的字串或 t('key')。\n" +
                "日誌、console、屬性說明、已有 isZh／isEn 分支的雙語寫法、依語系分檔的 *.zh-TW.cshtml 不會被算進來。\n" +
                $"確定要保留中文（例：寫入稽核紀錄的資料、語言選單裡的「繁體中文」），請登錄到 {BaselineFile}／{LocalBaselineFile} 並註明理由。\n\n" +
                string.Join("\n", offenders));

            var stale = staleCheckable.Where(k => !used.Contains(k)).ToList();
            Assert.True(stale.Count == 0,
                "以下 baseline 條目已對不到任何程式碼（已修正或已改寫），請從 baseline 移除：\n" +
                string.Join("\n", stale.Select(s => s.Replace("\t", "  "))));
        }

        private static List<Hit> CSharpHits(string root, out int probed)
        {
            var hits = new List<Hit>();
            probed = 0;
            foreach (var file in SourceFiles(root))
            {
                probed++;
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var src = File.ReadAllText(file);
                foreach (var (start, end) in Literals(src, out var masked))
                {
                    var literal = src[start..end];
                    if (!Cjk.IsMatch(literal) || IsDeliberate(masked, start)) continue;
                    hits.Add(new Hit(rel, src.AsSpan(0, start).Count('\n') + 1, literal));
                }
            }
            return hits;
        }

        // ── 畫面（.cshtml）、腳本（.js）、手機靜態頁（.html）────────────────────────────
        // 模組畫面另有 Cx6/Cx7（部署專案）；這裡補的是 Views／Pages／Areas 與 wwwroot 的 JS／HTML，
        // 以及 Core 自己（Core 沒有 Cx 規則）。

        private static readonly HashSet<string> ViewSkipped = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", "Migrations", "tools", "scripts",
            "lib", "libs", "docs", "__tests__",
            "SmartFactory.MES.Tests", "SmartFactory.MES.IntegrationTests",
            "SmartFactory.MES.UITests", "SmartFactory.MES.MachineId",
            "SmartFactory.MES.LicenseAuthority", "SmartFactory.MES.Security",
        };

        private static readonly Regex LocalizerCall = new(
            @"@?(?:L|Localizer|SL|SharedLocalizer|ViewLocalizer)\[\s*""[^""]*""\s*(?:,[^\]]*)?\]", RegexOptions.Compiled);
        private static readonly Regex ConsoleCall = new(@"\bconsole\.\w+\s*\(", RegexOptions.Compiled);
        private static readonly Regex PerLanguageView = new(@"\.(?:zh-TW|en-US)\.cshtml$", RegexOptions.Compiled);
        private static readonly Regex MarkupComment = new(
            @"@\*.*?\*@|<!--.*?-->|/\*.*?\*/|(?<![:'""/\\])//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ScriptOrStyleBody = new(
            @"(<(script|style)\b[^>]*>)(.*?)(</\2\s*>)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex Tag = new(@"<(/?)([A-Za-z][\w-]*)([^>]*)>", RegexOptions.Compiled);
        private static readonly Regex TextAttribute = new(
            @"\b(placeholder|title|aria-label|alt)\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
            { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

        private static List<Hit> ViewAndScriptHits(string root, out int probed)
        {
            var hits = new List<Hit>();
            probed = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(ViewSkipped.Contains)) continue;
                IEnumerable<(int Line, string Text)> found;
                if (rel.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                {
                    if (PerLanguageView.IsMatch(rel)) continue;   // 依語系分檔的版本，本來就是單一語言
                    found = CshtmlHits(File.ReadAllText(file));
                }
                else if (rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !rel.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                    found = JsHits(File.ReadAllText(file));
                else if (rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && rel.StartsWith("wwwroot/"))
                    found = HtmlHits(File.ReadAllText(file));
                else continue;
                probed++;
                hits.AddRange(found.Select(f => new Hit(rel, f.Line, f.Text)));
            }
            return hits;
        }

        private static string Mask(string text, Regex pattern) =>
            pattern.Replace(text, m => Regex.Replace(m.Value, @"[^\n]", " "));

        /// <summary>
        /// 逐行判斷（Razor 混雜 HTML／C#／JS，切不出可靠的字串常值）。註解先遮掉；L["…"] 不算；
        /// 同行有 isEn／isZh，或本行以 : ／ ? 開頭且前三行有語系判斷（跨行三元）視為雙語寫法。
        /// </summary>
        internal static IEnumerable<(int Line, string Text)> CshtmlHits(string text)
        {
            var lines = Mask(text, MarkupComment).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var l = LocalizerCall.Replace(lines[i], "");
                if (!Cjk.IsMatch(l) || LanguageBranch.IsMatch(l) || ConsoleCall.IsMatch(l)) continue;
                if (Regex.IsMatch(l, @"^\s*[:?]") && lines[Math.Max(0, i - 3)..i].Any(LanguageBranch.IsMatch)) continue;
                yield return (i + 1, lines[i].Trim());
            }
        }

        /// <summary>JS 字串常值（'…'、"…"、`…`）；所在敘述是 console.* 或有語系判斷者不算。</summary>
        internal static IEnumerable<(int Line, string Text)> JsHits(string src)
        {
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/' && (i == 0 || src[i - 1] != ':' && src[i - 1] != '\\'))
                {
                    var j = src.IndexOf('\n', i); i = j < 0 ? n : j; continue;
                }
                if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    var j = src.IndexOf("*/", i + 2, StringComparison.Ordinal); i = j < 0 ? n : j + 2; continue;
                }
                if (c is '\'' or '"' or '`')
                {
                    int j = i + 1;
                    while (j < n && src[j] != c)
                    {
                        if (src[j] == '\\') j++;
                        else if (c != '`' && src[j] == '\n') break;
                        j++;
                    }
                    var end = Math.Min(j + 1, n);
                    var literal = src[i..end];
                    if (Cjk.IsMatch(literal))
                    {
                        // 判斷範圍是「敘述起點（; { }）到本行行尾」：跨行的 console.warn('…' +\n '…') 也要認得
                        var ss = src.LastIndexOfAny(new[] { ';', '{', '}' }, Math.Max(0, i - 1)) + 1;
                        var le = src.IndexOf('\n', i); le = le < 0 ? n : le;
                        var statement = src[ss..le];
                        if (!ConsoleCall.IsMatch(statement) && !LanguageBranch.IsMatch(statement))
                            yield return (src.AsSpan(0, i).Count('\n') + 1, literal);
                    }
                    i = end; continue;
                }
                i++;
            }
        }

        /// <summary>
        /// 手機靜態頁：文字節點要由所屬元素的 data-i18n 提供譯文（原文只是後備），
        /// placeholder／title／aria-label／alt 要有對應的 data-i18n-{屬性}。script／style 內容不看（JS 另掃）。
        /// </summary>
        internal static IEnumerable<(int Line, string Text)> HtmlHits(string src)
        {
            var masked = Mask(src, MarkupComment);
            masked = ScriptOrStyleBody.Replace(masked, m => m.Groups[1].Value + Regex.Replace(m.Groups[3].Value, @"[^\n]", " ") + m.Groups[4].Value);
            var stack = new List<(string Name, string Attrs)>();
            int pos = 0;
            int LineAt(int at) => masked.AsSpan(0, at).Count('\n') + 1;
            foreach (Match m in Tag.Matches(masked))
            {
                var text = masked[pos..m.Index];
                if (Cjk.IsMatch(text))
                {
                    var owner = stack.Count > 0 ? stack[^1].Attrs : "";
                    if (!Regex.IsMatch(owner, @"\bdata-i18n(?:-html)?\s*="))   // -html：整段（含 <strong>）由譯文取代
                        yield return (LineAt(pos + (text.Length - text.TrimStart().Length)), Regex.Replace(text.Trim(), @"\s+", " "));
                }
                pos = m.Index + m.Length;

                var name = m.Groups[2].Value;
                var attrs = m.Groups[3].Value;
                if (m.Groups[1].Value == "/")
                {
                    var k = stack.FindLastIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) stack.RemoveRange(k, stack.Count - k);
                    continue;
                }
                foreach (Match a in TextAttribute.Matches(attrs))
                {
                    if (Cjk.IsMatch(a.Groups[2].Value)
                        && !Regex.IsMatch(attrs, @"\bdata-i18n-" + Regex.Escape(a.Groups[1].Value.ToLowerInvariant()) + @"\s*="))
                        yield return (LineAt(m.Index), $"{a.Groups[1].Value}=\"{a.Groups[2].Value}\"");
                }
                if (!VoidElements.Contains(name) && !attrs.TrimEnd().EndsWith('/'))
                    stack.Add((name, attrs));
            }
        }

        [Theory]
        [InlineData("<h1>中文標題</h1>", 1)]
        [InlineData("<h1>@L[\"Title\"]</h1>", 0)]
        [InlineData("<h2>@(isEn ? \"Log in\" : \"系統登入\")</h2>", 0)]
        [InlineData("<p>@(isEn\n    ? \"Keep these codes safe.\"\n    : \"請妥善保存復原碼。\")</p>", 0)]
        [InlineData("@* 說明 *@\n<!-- 註解 -->\n<div></div>", 0)]
        [InlineData("<script>\n  console.warn('載入失敗');\n</script>", 0)]
        public void Cshtml_Classifier(string view, int expected) =>
            Assert.Equal(expected, CshtmlHits(view).Count());

        [Theory]
        [InlineData("alert('權限不足');", 1)]
        [InlineData("console.warn('[Push] 初始化失敗', e);", 0)]
        [InlineData("// 說明\n/* 區塊 */ const a = 'ok';", 0)]
        [InlineData("const msg = `操作失敗（${res.status}）`;", 1)]
        [InlineData("const url = 'https://x.example/'; // 中文註解", 0)]
        [InlineData("label = isEn ? 'Retry' : '重試';", 0)]
        [InlineData("if (!fn) {\n  console.warn('找不到：' + name +\n               '（請確認已定義）');\n}", 0)]
        [InlineData("if (isEn) { a(); }\nshowToast('權限不足');", 1)]
        public void Js_Classifier(string js, int expected) =>
            Assert.Equal(expected, JsHits(js).Count());

        [Theory]
        [InlineData("<button data-i18n=\"sync.now\"><i class=\"bi\"></i>立即同步</button>", 0)]
        [InlineData("<button><i class=\"bi\"></i>立即同步</button>", 1)]
        [InlineData("<input data-i18n-placeholder=\"login.user\"\n       placeholder=\"請輸入帳號\">", 0)]
        [InlineData("<input placeholder=\"請輸入帳號\">", 1)]
        [InlineData("<title>首頁</title>", 1)]
        [InlineData("<title data-i18n=\"home.title\">首頁</title>", 0)]
        [InlineData("<script>const a = '中文';</script><style>/* 中文 */</style>", 0)]
        [InlineData("<!-- 中文註解 --><br><span data-i18n=\"a\">文字</span>", 0)]
        [InlineData("<div data-i18n-html=\"iqc.banner\"><strong>MIL</strong>：全批拒收。</div>", 0)]
        public void Html_Classifier(string html, int expected) =>
            Assert.Equal(expected, HtmlHits(html).Count());

        [Theory]
        [InlineData("var s = \"中文\";", false)]
        [InlineData("_logger.LogWarning(\"申請單 {Id} 找不到\", id);", true)]
        [InlineData("[PermissionGateExempt(\"只讀自己的資料\")]\npublic IActionResult X() => Ok();", true)]
        [InlineData("[HttpPost]\npublic IActionResult X() => Json(new { message = \"中文\" });", false)]
        [InlineData("{\n[AllowAnonymous]\n[PermissionGateExempt(\"錯誤頁\")]\npublic class E", true)]
        [InlineData("Log.Warning(\"   到期：{Expiry}\", d);", true)]
        [InlineData("Title = isZh ? \"使用者管理\" : \"User Management\";", true)]
        [InlineData("public override IdentityError X() => IsZhTw\n ? new IdentityError { Description = \"中文\" }\n : base.X();", true)]
        [InlineData("if (isEn) { a(); }\nreturn Ok(new { message = \"中文\" });", false)]
        [InlineData("throw new InvalidOperationException($\"找不到 {(x ? \"a\" : \"b\")}\");", false)]
        public void Classifier_RecognizesDeliberateContexts(string code, bool deliberate)
        {
            var hits = Literals(code, out var masked).Where(r => Cjk.IsMatch(code[r.Start..r.End])).ToList();
            Assert.Single(hits);
            Assert.Equal(deliberate, IsDeliberate(masked, hits[0].Start));
        }

        [Fact]
        public void Tokenizer_SkipsCommentsAndHandlesStringForms()
        {
            const string code = "// \"中\"\n/* \"中\" */ var a = @\"x\"\"中\"\"\"; var b = \"\"\"\n中\n\"\"\"; var c = '\"'; var d = $\"{e(\"中\")}\";";
            var lits = Literals(code, out _).Select(r => code[r.Start..r.End]).ToList();
            Assert.Equal(new[] { "@\"x\"\"中\"\"\"", "\"\"\"\n中\n\"\"\"", "$\"{e(\"中\")}\"" }, lits);
        }

        /// <summary>
        /// 讀兩份 baseline。過期檢查：有 Local 檔（＝在部署專案裡跑）時只檢查 Local 的條目——
        /// 部署專案的 Program.cs 等檔與 Core 不同，Core 條目在那裡對不到是正常的，
        /// 而部署專案也不能改 Core 的 baseline；Core 條目的過期由 Core 自己的 CI 抓。
        /// </summary>
        private static (HashSet<string> Literals, HashSet<string> WholeFiles, List<string> StaleCheckable) LoadBaseline(string root)
        {
            var literals = new HashSet<string>(StringComparer.Ordinal);
            var wholeFiles = new HashSet<string>(StringComparer.Ordinal);
            var staleCheckable = new List<string>();
            var hasLocal = File.Exists(Path.Combine(root, "SmartFactory.MES.Tests", LocalBaselineFile));
            foreach (var name in new[] { BaselineFile, LocalBaselineFile })
            {
                var path = Path.Combine(root, "SmartFactory.MES.Tests", name);
                if (!File.Exists(path)) continue;
                var checkStale = !hasLocal || name == LocalBaselineFile;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (raw.Length == 0 || raw.StartsWith('#')) continue;
                    var tab = raw.IndexOf('\t');
                    Assert.True(tab > 0, $"{name} 格式錯誤（應為「路徑<TAB>字面原文」或「路徑<TAB>*」）：{raw}");
                    if (raw[(tab + 1)..] == "*") wholeFiles.Add(raw[..tab]);
                    else literals.Add(raw);
                    if (checkStale) staleCheckable.Add(raw);
                }
            }
            return (literals, wholeFiles, staleCheckable);
        }

        private static string Escape(string literal) =>
            literal.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");

        /// <summary>literal 所在敘述（遮蔽後的程式碼中，往前找到敘述起點）是否屬刻意保留的情境。</summary>
        private static bool IsDeliberate(string masked, int literalStart)
        {
            var prefix = StatementPrefix(masked, literalStart);
            if (LoggerCall.IsMatch(prefix) || LanguageBranch.IsMatch(prefix)) return true;

            // 屬性參數：敘述到字串為止只由屬性組成（可多個疊放），且字串仍在某一對方括號內
            int depth = 0;
            foreach (var ch in prefix)
            {
                if (ch == '[') depth++;
                else if (ch == ']') depth--;
                else if (depth == 0 && !char.IsWhiteSpace(ch)) return false;
            }
            return depth > 0;
        }

        private static string StatementPrefix(string masked, int pos)
        {
            int depth = 0;
            for (int i = pos - 1; i >= 0; i--)
            {
                var ch = masked[i];
                // 深度 0 的 } 是前一個區塊的結尾＝本敘述起點（否則 if (isEn) { … } 之後的敘述會被誤判為雙語分支）
                if (ch == '}' && depth == 0) return masked[(i + 1)..pos];
                if (ch is ')' or ']' or '}') depth++;
                else if (ch is '(' or '[') { if (depth > 0) depth--; }
                else if (ch == '{')
                {
                    if (depth > 0) { depth--; continue; }
                    if (InitializerBrace.IsMatch(masked.AsSpan(Math.Max(0, i - 200), i - Math.Max(0, i - 200)).ToString())) continue;
                    return masked[(i + 1)..pos];
                }
                else if (ch == ';' && depth == 0) return masked[(i + 1)..pos];
            }
            return masked[..pos];
        }

        /// <summary>
        /// 列出字串常值的範圍；<paramref name="masked"/> 是把註解與字串／字元常值內容換成空白後的原始碼
        /// （長度不變），供判斷上下文用。
        /// </summary>
        private static List<(int Start, int End)> Literals(string src, out string masked)
        {
            var result = new List<(int, int)>();
            var sb = new StringBuilder(src);
            int i = 0;
            while (i < src.Length)
            {
                if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    var j = src.IndexOf('\n', i); j = j < 0 ? src.Length : j;
                    Blank(sb, i, j); i = j; continue;
                }
                if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    var j = src.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? src.Length : j + 2;
                    Blank(sb, i, j); i = j; continue;
                }
                if (src[i] == '\'')
                {
                    var j = i + 1;
                    while (j < src.Length && src[j] != '\'' && src[j] != '\n') j += src[j] == '\\' ? 2 : 1;
                    j = Math.Min(j + 1, src.Length);
                    Blank(sb, i, j); i = j; continue;
                }
                if (IsStringStart(src, i))
                {
                    var end = ReadString(src, i);
                    result.Add((i, end));
                    Blank(sb, i, end); i = end; continue;
                }
                i++;
            }
            masked = sb.ToString();
            return result;
        }

        private static void Blank(StringBuilder sb, int from, int to)
        {
            for (int k = from; k < to; k++) if (sb[k] != '\n') sb[k] = ' ';
        }

        private static bool IsStringStart(string s, int i)
        {
            int j = i;
            while (j < s.Length && (s[j] == '$' || s[j] == '@')) j++;
            if (j >= s.Length || s[j] != '"') return false;
            // $ 或 @ 不能是識別字的一部分（例：@this 不是字串）
            return j == i || i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_');
        }

        private static int ReadString(string s, int i)
        {
            int j = i, dollars = 0; bool verbatim = false;
            while (s[j] == '$' || s[j] == '@') { if (s[j] == '$') dollars++; else verbatim = true; j++; }

            int quotes = 0;
            while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;
            if (quotes >= 3)
            {
                var close = new string('"', quotes);
                var k = s.IndexOf(close, j + quotes, StringComparison.Ordinal);
                return k < 0 ? s.Length : k + quotes;
            }

            j++; // 開頭引號
            while (j < s.Length)
            {
                var ch = s[j];
                if (verbatim && ch == '"' && j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                if (!verbatim && ch == '\\') { j += 2; continue; }
                if (ch == '"') return j + 1;
                if (dollars > 0 && ch == '{')
                {
                    if (j + 1 < s.Length && s[j + 1] == '{') { j += 2; continue; }
                    j = SkipHole(s, j + 1); continue;
                }
                if (!verbatim && ch == '\n') return j; // 未閉合：停在行尾，避免吞掉後續程式碼
                j++;
            }
            return s.Length;
        }

        /// <summary>插值洞 {…} 內可能有巢狀字串，要遞迴跳過，否則洞內的引號會被誤認為字串結尾。</summary>
        private static int SkipHole(string s, int j)
        {
            int depth = 1;
            while (j < s.Length && depth > 0)
            {
                if (IsStringStart(s, j)) { j = ReadString(s, j); continue; }
                if (s[j] == '\'') { j++; while (j < s.Length && s[j] != '\'') j += s[j] == '\\' ? 2 : 1; j++; continue; }
                if (s[j] == '{') depth++;
                else if (s[j] == '}') depth--;
                j++;
            }
            return j;
        }
    }
}
