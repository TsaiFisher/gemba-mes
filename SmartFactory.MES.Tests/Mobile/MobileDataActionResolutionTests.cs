using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 手機頁面上每一個 <c>data-action</c>，都要有一個同名的全域函式接得住。
    ///
    /// <para><b>失敗長什麼樣</b>：<c>wwwroot/js/data-action.js</c> 的派送是
    /// <c>var fn = window[name]; if (typeof fn !== 'function') { console.warn(…); return; }</c>——
    /// 名字對不上就<b>靜靜返回</b>，只在 console 留一行警告。使用者看到的是
    /// 「這顆按鈕按下去毫無反應」，而那與「網路慢」「權限不足」在畫面上分不出來。
    /// 手機端更嚴重：現場人員沒有開發者工具，也不會回報 console 訊息。</para>
    ///
    /// <para><b>為什麼是靜態掃描而不是跑瀏覽器</b>：這個缺陷的成因是**打錯字或改名時漏改一邊**，
    /// 而那在原始碼上就看得出來。跑瀏覽器要先登入、要有資料、要點得到那顆按鈕——
    /// 成本高得多，而且點不到的按鈕仍然驗不到。</para>
    ///
    /// <para>⚠️ <b>這支測試證明「名字接得上」，不證明「行為正確」。</b>
    /// 函式存在但做錯事，這裡一樣是綠的。</para>
    /// </summary>
    public class MobileDataActionResolutionTests
    {
        /// <summary>
        /// <c>data-action.js</c> 之外的來源也會提供全域函式（nav.js 的導覽、auth.js 的登出…）。
        /// 掃描範圍涵蓋整個 <c>wwwroot/mobile</c> 與頁面實際載入的共用腳本。
        /// </summary>
        private static readonly string[] SharedScripts =
        {
            "wwwroot/js/data-action.js",
            "wwwroot/mobile/js/nav.js",
            "wwwroot/mobile/js/i18n.js",
            "wwwroot/mobile/js/api.js",
            "wwwroot/mobile/js/auth.js",
            "wwwroot/mobile/js/offline-queue.js",
        };

        [Fact]
        public void 每個手機頁面的data_action都找得到同名全域函式()
        {
            var root = RepoRoot();
            var mobileDir = Path.Combine(root, "wwwroot", "mobile");
            Assert.True(Directory.Exists(mobileDir), $"找不到 {mobileDir}");

            var defined = CollectGlobalFunctionNames(root, mobileDir);

            // 下限守門：掃不到函式時這支會變成「零項全過」的裝飾品。
            Assert.True(defined.Count >= 20,
                $"只掃到 {defined.Count} 個全域函式，遠少於預期——多半是掃描壞了，不是函式真的變少。");

            var missing = new List<string>();
            foreach (var html in Directory.EnumerateFiles(mobileDir, "*.html", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(html);
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"data-action(?:-\w+)?=""([A-Za-z_$][\w$]*)"""))
                {
                    var name = m.Groups[1].Value;
                    if (!defined.Contains(name))
                        missing.Add($"{Path.GetRelativePath(root, html)} → {name}");
                }
            }

            Assert.True(missing.Count == 0,
                "以下 data-action 找不到同名的全域函式，按下去會**毫無反應**"
                + "（data-action.js 只 console.warn 就 return，畫面上沒有任何訊息）：\n  "
                + string.Join("\n  ", missing));
        }

        [Fact]
        public void 掃描抓得到刻意放進去的錯字()
        {
            // 反面對照。少了這一支，一個「永遠回空清單」的掃描（例如正則寫錯、
            // 目錄找錯）也會讓上面那支變綠——而那正是最容易發生的失效方式。
            var defined = new HashSet<string>(StringComparer.Ordinal) { "reviewApproval" };

            var html = "<button data-action=\"reviewApproval\"></button>"
                     + "<button data-action=\"reviewAproval\"></button>";

            var found = Regex.Matches(html, @"data-action(?:-\w+)?=""([A-Za-z_$][\w$]*)""")
                .Select(m => m.Groups[1].Value)
                .Where(n => !defined.Contains(n))
                .ToList();

            Assert.Equal(new[] { "reviewAproval" }, found);
        }

        [Fact]
        public void 掃描器自我檢查_所有手機腳本的大括號都必須平衡()
        {
            // 🚨 這一支守的是**上面那支測試的可信度**，不是產品程式。
            //
            // 深度追蹤一旦數歪，症狀不是「測試失敗」而是「測試說謊」：
            // 全域函式被誤判成非全域（假警報），或非全域被誤判成全域（**漏掉真缺陷**）。
            // 後者完全無聲。而數歪的原因兩次都是 StripNonCode 沒認得某種字面
            // （正則裡的雙引號、巢狀樣板字面），那在檔尾深度上看得出來——不為 0 就是數歪了。
            var root = RepoRoot();
            var bad = new List<string>();

            foreach (var js in Directory.EnumerateFiles(
                         Path.Combine(root, "wwwroot", "mobile"), "*.js", SearchOption.AllDirectories))
            {
                if (js.Contains("__tests__")) continue;

                var text = StripNonCode(File.ReadAllText(js));
                int depth = 0;
                foreach (var c in text)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth = Math.Max(0, depth - 1);
                }
                if (depth != 0) bad.Add($"{Path.GetRelativePath(root, js)}（檔尾深度 {depth}，應為 0）");
            }

            Assert.True(bad.Count == 0,
                "以下手機腳本在 StripNonCode 之後大括號不平衡，代表掃描器沒認得某種字面"
                + "（正則？樣板？），而上面那支 data-action 測試的結果因此**不可信**：\n  "
                + string.Join("\n  ", bad));
        }

        // ── 掃描 ─────────────────────────────────────────────────────────────

        private static HashSet<string> CollectGlobalFunctionNames(string root, string mobileDir)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            var files = Directory.EnumerateFiles(mobileDir, "*.js", SearchOption.AllDirectories)
                .Where(f => !f.Contains("__tests__"))
                .Concat(SharedScripts.Select(s => Path.Combine(root, s)).Where(File.Exists));

            foreach (var js in files)
            {
                var raw = File.ReadAllText(js);
                var text = StripNonCode(raw);

                // 只認**巢狀深度 0** 的 function 宣告——那些才會變成 window 的屬性
                // （這些檔案都是傳統腳本，非 module）。
                //
                // ⚠️ 兩個都踩過的坑：
                //  ① 不能用 `^function`（要求第 0 欄）。這些 .js 是從 home.html 的內聯
                //     <script> 外部化來的，**整份縮排 8 格**，於是 syncNow／openPlantSwitch
                //     等一大批全域函式全部掃不到，這支測試第一版因此紅了 25 筆假違規。
                //  ② 也不能只放寬成 `^\s*function`——那會把**巢狀**函式一起算進來，
                //     而巢狀函式不是全域。守門會因此漏掉真正的打錯字。
                //     所以改成追蹤大括號深度，並先把註解與字串拿掉（否則大括號會數歪）。
                int depth = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '{') { depth++; continue; }
                    if (c == '}') { depth = Math.Max(0, depth - 1); continue; }
                    if (depth != 0 || c != 'f') continue;

                    var m = FunctionDecl.Match(text, i);
                    if (m.Success && m.Index == i) names.Add(m.Groups[1].Value);
                }

                // 明確掛上去的（window.foo = … / window['foo'] = …）——深度不拘，
                // 因為那本來就是「從任何地方掛上全域」的寫法。
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"window\.([A-Za-z_$][\w$]*)\s*="))
                    names.Add(m.Groups[1].Value);
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"window\[\s*['""]([A-Za-z_$][\w$]*)['""]\s*\]\s*="))
                    names.Add(m.Groups[1].Value);
            }

            return names;
        }

        private static readonly Regex FunctionDecl =
            new(@"function\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// 把註解、字串、正則字面與樣板字面換成空白（長度不變，方便沿用索引），
        /// 好讓大括號深度數得準。
        ///
        /// <para>⚠️ <b>這裡的每一段都是被真實檔案打臉之後才加上的</b>，不是預先設想：</para>
        /// <list type="number">
        /// <item><b>正則字面</b>：<c>ipqc-process.page.js</c> 有
        ///   <c>.replace(/"/g, '&amp;quot;')</c>——正則裡的雙引號被當成字串開頭，
        ///   之後整份檔案的深度全歪，<c>submitIpqc</c> 因此被誤判成非全域。</item>
        /// <item><b>巢狀樣板字面</b>：<c>schedule.page.js</c> 有
        ///   <c>${flags ? `&lt;div&gt;${flags}&lt;/div&gt;` : ''}</c>——
        ///   從外層反引號直接找下一個反引號，會停在**內層的開頭**。
        ///   所以樣板要追蹤 <c>${{</c> 的巢狀層數。</item>
        /// </list>
        /// <para>正則與除號的區分用的是標準啟發式（看前一個有意義字元），
        /// 不是完整的 JS 詞法分析。守門測試本身若過於複雜，它自己就會變成缺陷來源——
        /// 所以有一支「所有手機腳本的括號都必須平衡」的自我檢查在下面接著。</para>
        /// </summary>
        private static string StripNonCode(string src)
        {
            var b = src.ToCharArray();
            int i = 0, n = b.Length;

            char PrevSignificant(int j)
            {
                int k = j - 1;
                while (k >= 0 && (b[k] == ' ' || b[k] == '\t')) k--;
                return k >= 0 ? b[k] : '\n';
            }

            while (i < n)
            {
                char c = b[i];

                if (c == '/' && i + 1 < n && b[i + 1] == '/')
                {
                    while (i < n && b[i] != '\n') b[i++] = ' ';
                    continue;
                }
                if (c == '/' && i + 1 < n && b[i + 1] == '*')
                {
                    while (i < n && !(b[i] == '*' && i + 1 < n && b[i + 1] == '/'))
                    {
                        if (b[i] != '\n') b[i] = ' ';
                        i++;
                    }
                    if (i + 1 < n) { b[i] = ' '; b[i + 1] = ' '; i += 2; }
                    continue;
                }
                if (c == '/' && RegexCanStartHere.Contains(PrevSignificant(i)))
                {
                    b[i++] = ' ';
                    while (i < n && b[i] != '/')
                    {
                        if (b[i] == '\n') break;               // 正則不跨行；跨行代表判斷錯了，停手
                        if (b[i] == '\\') { b[i] = ' '; i++; }
                        if (i < n) { b[i] = ' '; i++; }
                    }
                    if (i < n && b[i] == '/') b[i++] = ' ';
                    continue;
                }
                if (c == '`')
                {
                    b[i++] = ' ';
                    int interp = 0;
                    while (i < n)
                    {
                        if (b[i] == '\\')
                        {
                            b[i] = ' '; i++;
                            if (i < n) { b[i] = ' '; i++; }
                            continue;
                        }
                        if (b[i] == '$' && i + 1 < n && b[i + 1] == '{')
                        {
                            interp++; b[i] = ' '; b[i + 1] = ' '; i += 2; continue;
                        }
                        if (b[i] == '}' && interp > 0) { interp--; b[i] = ' '; i++; continue; }
                        if (b[i] == '`' && interp == 0) { b[i] = ' '; i++; break; }
                        if (b[i] != '\n') b[i] = ' ';
                        i++;
                    }
                    continue;
                }
                if (c is '"' or '\'')
                {
                    char quote = c;
                    b[i++] = ' ';
                    while (i < n && b[i] != quote)
                    {
                        if (b[i] == '\\') { b[i] = ' '; i++; }
                        if (i < n && b[i] != '\n') b[i] = ' ';
                        i++;
                    }
                    if (i < n) b[i++] = ' ';
                    continue;
                }
                i++;
            }
            return new string(b);
        }

        private static readonly HashSet<char> RegexCanStartHere =
            new("(,=:[!&|?{};+-*%^~<>\n".ToCharArray());

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
