using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 教學系統的**守門**（會擋 build）。與 <see cref="TutorCourseFacts"/> 的分工是刻意的：
    /// 那一支只量測、一條門檻都不設；這一支只放「使用者看得到的錯」，不放規模數字、不放內容品質。
    ///
    /// <para><b>為什麼教學頁需要專屬守門：</b>它是全手冊唯一「內容由程式從別的頁面產生」的一區。
    /// 手冊其餘 147 頁是人寫的，改壞了會被 code review 看到；課程總覽是產生的，
    /// 流程圖一改它就過期，而過期的樣子是「某條課程從清單上消失」或「連結指向不存在的課程」——
    /// 兩者在瀏覽器裡都不會報錯，只是靜靜地少一塊。</para>
    /// </summary>
    public class UserManualTutorTests
    {
        private static readonly HtmlParser Html = new();

        private readonly ITestOutputHelper _out;

        public UserManualTutorTests(ITestOutputHelper output) => _out = output;

        private static string TutorDir() =>
            Path.Combine(ManualCorpusFacts.ManualRoot(), "tutor");

        private static IDocument Page(string name) =>
            Html.ParseDocument(File.ReadAllText(Path.Combine(TutorDir(), name)));

        private static IEnumerable<string> TutorPages() =>
            Directory.EnumerateFiles(TutorDir(), "*.html").OrderBy(p => p, StringComparer.Ordinal);

        // ── 索引建置的前提 ────────────────────────────────────────────────

        /// <summary>
        /// <c>DocIndexBuilder</c> 掃 <c>docs/user/**/*.html</c>，**沒有排除機制也不吞例外**，
        /// 而 <c>ManualDocParser</c> 找不到 <c>main.content</c> 時會丟例外。
        /// 也就是說在這個目錄底下放一個沒有 <c>main.content</c> 的頁面，
        /// 會讓**整個手冊索引建置中斷、Q&amp;A 一起掛掉**——後果遠大於「這一頁查不到」。
        /// </summary>
        [Fact]
        public void 每個教學頁都有可解析的內容區()
        {
            foreach (var file in TutorPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var main = doc.QuerySelector("main.content");

                Assert.True(main is not null,
                    $"{Path.GetFileName(file)} 缺 main.content —— 這會讓整個手冊索引建置丟例外中斷，"
                    + "不只是這一頁檢索不到。");
                Assert.True(main!.QuerySelectorAll("h2").Length > 0,
                    $"{Path.GetFileName(file)} 的 main.content 沒有任何 h2 —— "
                    + "ManualDocParser 以標題切塊，沒有標題就切不出有意義的 chunk。");
            }
        }

        /// <summary>
        /// 教學頁沒有對應的 MVC controller，<c>MenuKey</c> 必然為空，
        /// 會落進權限過濾的「模組目錄頁」分支，而該分支的可讀模組集合由使用者的 MenuKey 推得，
        /// **永遠不可能包含 <c>tutor</c>**。少了白名單，這些頁索引得進去、卻對每個使用者都檢索不到，
        /// 而且完全沒有訊號（與 <c>mobile/</c> 當初同一個坑）。
        /// </summary>
        [Fact]
        public void 教學目錄在公開白名單內()
        {
            Assert.Contains("tutor", Modules.QA.Contracts.ManualCorpusPolicy.PublicModules);
        }

        // ── 課程總覽與流程圖的一致性 ──────────────────────────────────────

        /// <summary>
        /// 課程總覽是**從 12 頁流程圖產生的靜態內容**（必須靜態：JS 產的東西索引器看不到）。
        /// 產生物與來源之間沒有執行期關聯，所以只能靠這條測試綁住：
        /// 少一條 → 該課程永遠沒有入口；多一條 → 連結指向不存在的課程，播放器只會顯示空白。
        /// </summary>
        [Fact]
        public void 課程總覽涵蓋且僅涵蓋流程圖上的課程()
        {
            var expected = TutorCourseFacts.AllCourses()
                .Select(c => c.Id ?? "")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.DoesNotContain("", expected);

            var actual = Page("index.html")
                .QuerySelectorAll("main.content a.tutor-card")
                .Select(a => a.GetAttribute("href") ?? "")
                .Select(h => h.Contains("?flow=", StringComparison.Ordinal)
                    ? h[(h.IndexOf("?flow=", StringComparison.Ordinal) + 6)..]
                    : "")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
            var extra = actual.Except(expected, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0 && extra.Count == 0,
                "tutor/index.html 與流程圖不同步（重跑產生器即可）：\n"
                + (missing.Count > 0 ? "  總覽少了：" + string.Join(", ", missing) + "\n" : "")
                + (extra.Count > 0 ? "  總覽多了（指向不存在的課程）：" + string.Join(", ", extra) + "\n" : ""));

            Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>
        /// 卡片上寫著「N 站」。這個數字是產生當下抄下來的，流程圖之後增減節點它不會自己變，
        /// 而使用者是靠它決定要不要點進來的。
        /// </summary>
        [Fact]
        public void 課程卡片的站數與流程圖一致()
        {
            var expected = TutorCourseFacts.AllCourses()
                .ToDictionary(c => c.Id!,
                              c => c.Stops.Count(s => s.Kind is "node" or "ext" or "evt"),
                              StringComparer.Ordinal);

            var wrong = new List<string>();

            foreach (var card in Page("index.html").QuerySelectorAll("main.content a.tutor-card"))
            {
                var href = card.GetAttribute("href") ?? "";
                var id = href[(href.IndexOf("?flow=", StringComparison.Ordinal) + 6)..];
                var text = card.QuerySelector(".tutor-card-meta [lang=zh]")?.TextContent ?? "";
                var n = int.TryParse(new string(text.TakeWhile(char.IsDigit).ToArray()), out var v) ? v : -1;

                if (expected.TryGetValue(id, out var want) && want != n)
                    wrong.Add($"{id}：卡片寫 {n} 站、流程圖是 {want} 站");
            }

            Assert.True(wrong.Count == 0,
                "課程卡片的站數已過期（重跑產生器即可）：\n" + string.Join("\n", wrong));
        }

        /// <summary>
        /// <c>tutor.js</c> 只拿到 <c>?flow=</c> 一個字串，必須**從 id 自己推出課程住在哪一頁**
        /// （<c>flow-pur-…</c> → <c>../pur/index.html</c>、<c>flow-main-…</c> → <c>../index.html</c>）。
        /// 推導成立的前提是模組目錄名不含連字號；哪天有人開了 <c>work-order/</c> 這種目錄，
        /// 那一頁的所有課程都會靜默指到不存在的位址（播放器只顯示「找不到課程」）。
        ///
        /// <para>這條測試的替代方案是在 <c>tutor.js</c> 裡寫死一份模組清單——那就是第二份會漂移的真相。
        /// 寧可把約定釘在測試上。</para>
        /// </summary>
        [Fact]
        public void 課程id的模組前綴推得回原本的頁面()
        {
            foreach (var c in TutorCourseFacts.AllCourses())
            {
                var mod = c.Id!["flow-".Length..].Split('-')[0];
                var expected = c.Page == "(root)" ? "index.html" : $"{c.Page}/index.html";
                var derived = mod == "main" ? "index.html" : $"{mod}/index.html";

                Assert.True(expected == derived,
                    $"課程 {c.Id} 在 {expected}，但 id 前綴推出來的是 {derived}——"
                    + "tutor.js 會抓不到這條課程。模組目錄名不可含連字號。");
            }
        }

        /// <summary>
        /// <c>.step-row</c> 的 <c>data-shot</c> 是「這一步該配哪張截圖」的人工覆寫
        /// （預設規則是往後找同段落最近的截圖，猜不準時才用它）。
        /// 指到不存在的檔案時，播放器會顯示破圖——而破圖只有肉眼看得出來。
        /// </summary>
        [Fact]
        public void 步驟指定的截圖都存在()
        {
            var missing = new List<string>();

            foreach (var file in ManualCorpusFacts.AllPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var dir = Path.GetDirectoryName(file)!;

                foreach (var row in doc.QuerySelectorAll(".step-row[data-shot]"))
                {
                    var shot = row.GetAttribute("data-shot")!;
                    var zh = Path.Combine(dir, shot.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(zh))
                        missing.Add($"{Path.GetFileName(file)} 的 data-shot=\"{shot}\" 找不到檔案");

                    // 英文版走 img/en/ 這個既有慣例；缺了會退回中文截圖，不算錯，但值得知道。
                    var en = Path.Combine(dir, Path.GetDirectoryName(shot)!, "en", Path.GetFileName(shot));
                    if (!File.Exists(en))
                        missing.Add($"{Path.GetFileName(file)} 的 data-shot=\"{shot}\" 沒有英文版（{Path.GetFileName(en)}）");
                }
            }

            Assert.True(missing.Count == 0, string.Join("\n", missing));
        }

        // ── 熱點 ──────────────────────────────────────────────────────────

        private static JsonElement Json(string name) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(TutorDir(), name))).RootElement;

        /// <summary>
        /// 座標是 0–1 正規化的，播放器直接乘上 render 尺寸。超出範圍的值會把框畫到截圖外面，
        /// 而框是絕對定位的——它會蓋在頁面別的地方，看起來像版面壞掉而不是資料錯。
        /// </summary>
        [Fact]
        public void 熱點座標都在截圖範圍內()
        {
            var bad = new List<string>();

            foreach (var shot in Json("hotspots.json").EnumerateObject())
            {
                foreach (var ctrl in shot.Value.EnumerateObject())
                {
                    var v = ctrl.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();

                    if (v.Length != 4) { bad.Add($"{shot.Name} 的「{ctrl.Name}」不是 4 個數字"); continue; }
                    if (v.Any(x => x is < 0 or > 1)) bad.Add($"{shot.Name} 的「{ctrl.Name}」有值不在 0–1：[{string.Join(", ", v)}]");
                    if (v[0] + v[2] > 1.0001) bad.Add($"{shot.Name} 的「{ctrl.Name}」右緣超出截圖（x+w={v[0] + v[2]:F4}）");
                    if (v[1] + v[3] > 1.0001) bad.Add($"{shot.Name} 的「{ctrl.Name}」下緣超出截圖（y+h={v[1] + v[3]:F4}）");
                    if (v[2] <= 0 || v[3] <= 0) bad.Add($"{shot.Name} 的「{ctrl.Name}」寬或高是 0");
                }
            }

            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>
        /// 座標檔的鍵是截圖路徑。指到不存在的圖，那筆座標就永遠不會被用到——
        /// 沒有錯誤訊息，只是那一步沒有框，而「這一步本來就沒有框」是正常狀態，所以看不出差別。
        /// </summary>
        [Fact]
        public void 熱點的鍵都指向存在的截圖()
        {
            var missing = Json("hotspots.json").EnumerateObject()
                .Select(p => p.Name)
                .Where(k => !File.Exists(Path.Combine(ManualCorpusFacts.ManualRoot(),
                                                      k.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();

            Assert.True(missing.Count == 0,
                "hotspots.json 的鍵指向不存在的截圖：\n  " + string.Join("\n  ", missing));
        }

        /// <summary>
        /// 選擇器檔是**人類維護**的那一份，座標是從它產生的。這裡只驗格式，不驗選擇器選不選得到——
        /// 後者要開瀏覽器，由 <c>~/.cache/manual-tutor/hotspotgen</c> 在產生時失敗回報。
        /// </summary>
        [Fact]
        public void 熱點選擇器檔的格式完整()
        {
            var cfg = Json("hotspot-selectors.json");
            var bad = new List<string>();

            void CheckControl(JsonElement c, string where)
            {
                foreach (var f in new[] { "zh", "en", "selector" })
                    if (!c.TryGetProperty(f, out var v) || string.IsNullOrWhiteSpace(v.GetString()))
                        bad.Add($"{where}：缺 {f}");
            }

            foreach (var c in cfg.GetProperty("defaults").EnumerateArray())
                CheckControl(c, "defaults");

            foreach (var s in cfg.GetProperty("shots").EnumerateArray())
            {
                var img = s.GetProperty("img").GetString()!;
                if (string.IsNullOrWhiteSpace(s.GetProperty("route").GetString()))
                    bad.Add($"{img}：缺 route");

                // 中英兩版截圖都要在——只有一版的話另一語系會整張沒有熱點
                foreach (var p in new[] { img, img.Insert(img.LastIndexOf('/'), "/en") })
                    if (!File.Exists(Path.Combine(ManualCorpusFacts.ManualRoot(), p.Replace('/', Path.DirectorySeparatorChar))))
                        bad.Add($"{img}：截圖不存在 {p}");

                if (s.TryGetProperty("controls", out var cs))
                    foreach (var c in cs.EnumerateArray()) CheckControl(c, img);
            }

            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>
        /// 覆蓋率**只量測不擋**（規格第 7 節）：覆蓋率低是內容問題不是程式缺陷，
        /// 擋 build 只會逼人塞假座標。
        /// </summary>
        [Fact]
        public void 量測_熱點覆蓋率()
        {
            var hs = Json("hotspots.json");
            var shots = hs.EnumerateObject().Count();
            var boxes = hs.EnumerateObject().Sum(p => p.Value.EnumerateObject().Count());
            var configured = Json("hotspot-selectors.json").GetProperty("shots").EnumerateArray().Count();

            _out.WriteLine($"設定了 {configured} 張截圖（中英合計 {configured * 2}），"
                         + $"實際產出座標的有 {shots} 張、共 {boxes} 個框");

            // 只有兩條不變式：檔案不是空的（產生器跑過），以及沒有出現「設定裡沒有的截圖」。
            // **不驗「每張都要有框」**——報表頁沒有新增鈕是內容事實，不是缺陷；
            // 逼它一定要有框只會讓人去塞假座標。
            Assert.True(shots > 0, "hotspots.json 是空的——產生器沒跑過，或全部選擇器都失效。");
            Assert.True(shots <= configured * 2,
                $"座標檔有 {shots} 張截圖，但選擇器檔只設定了 {configured} 張（中英合計 {configured * 2}）——"
                + "多出來的那些是舊資料，重跑產生器。");
        }

        // ── 頁面本身 ──────────────────────────────────────────────────────

        /// <summary>
        /// 播放器的掛載點。改掉這個 id 而忘了改 <c>tutor.js</c>，頁面會維持在「請開啟 JavaScript」
        /// 的降級文案上——看起來像瀏覽器問題，不像程式缺陷。
        /// </summary>
        [Fact]
        public void 播放器有掛載點與無腳本降級文案()
        {
            var app = Page("player.html").QuerySelector("#tutorApp");

            Assert.True(app is not null, "player.html 缺 #tutorApp 掛載點。");
            Assert.True(app!.TextContent.Trim().Length > 0,
                "#tutorApp 是空的——腳本沒載入時使用者會看到一塊空白，沒有任何說明。");
        }

        /// <summary>
        /// CSP 是 <c>style-src 'self'</c> 且**完全沒有 nonce**。內聯 <c>&lt;style&gt;</c> 與
        /// <c>style="…"</c> 屬性都會被擋掉，而被擋掉的樣子是「版面沒套用」而不是報錯。
        /// 動態尺寸一律用 CSSOM 屬性指派（<c>el.style.left = …</c>），那條路不受 style-src 管。
        /// </summary>
        [Fact]
        public void 教學頁沒有內聯樣式()
        {
            foreach (var file in TutorPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var name = Path.GetFileName(file);

                Assert.True(doc.QuerySelectorAll("style").Length == 0,
                    $"{name} 有內聯 <style>，CSP style-src 'self' 沒有 nonce，會被擋掉。");

                var styled = doc.QuerySelectorAll("[style]").Select(e => e.OuterHtml[..Math.Min(90, e.OuterHtml.Length)]);
                Assert.True(!styled.Any(),
                    $"{name} 有 style 屬性，同樣會被 CSP 擋掉：\n  " + string.Join("\n  ", styled));
            }
        }

        /// <summary>
        /// 教學頁引用的 CSS/JS 必須真的存在。少一個檔案的表現是「頁面沒有樣式」或「語言切不動」，
        /// 兩者都不會在 console 以外留下痕跡。
        /// </summary>
        [Fact]
        public void 教學頁引用的靜態資產都存在()
        {
            foreach (var file in TutorPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var refs = doc.QuerySelectorAll("link[rel=stylesheet]").Select(e => e.GetAttribute("href"))
                    .Concat(doc.QuerySelectorAll("script[src]").Select(e => e.GetAttribute("src")))
                    .Where(h => !string.IsNullOrWhiteSpace(h))!
                    .Cast<string>();

                foreach (var href in refs)
                {
                    var target = Path.GetFullPath(Path.Combine(TutorDir(), href.Replace('/', Path.DirectorySeparatorChar)));
                    Assert.True(File.Exists(target),
                        $"{Path.GetFileName(file)} 引用了不存在的檔案：{href}（解析為 {target}）");
                }
            }
        }

        /// <summary>
        /// 雙語規範：手冊全站的顯示語言是純 CSS 選擇器決定的（<c>html[data-doc-lang]</c>），
        /// 漢字沒有包在 <c>[lang]</c> 裡就會**兩種語系都顯示**——英文版夾雜中文，且只有肉眼看得出來。
        /// （全手冊有 <c>UserManualBilingualTests</c> 在守，這裡是教學頁自己的一道，
        /// 因為總覽頁是產生的，產生器寫錯的話會一次錯 71 張卡片。）
        /// </summary>
        [Fact]
        public void 教學頁的漢字都包在語系標記內()
        {
            foreach (var file in TutorPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var main = doc.QuerySelector("main.content")!;
                var loose = new List<string>();

                foreach (var node in Descendants(main))
                {
                    if (node is not IText t || !t.Data.Any(IsHan)) continue;
                    if (Ancestors(t).Any(e => e.HasAttribute("lang"))) continue;
                    loose.Add(t.Data.Trim());
                }

                Assert.True(loose.Count == 0,
                    $"{Path.GetFileName(file)} 有漢字不在 [lang] 標記內（英文版會夾雜中文）：\n  "
                    + string.Join("\n  ", loose.Take(10)));
            }

            static bool IsHan(char c) => c >= 0x4E00 && c <= 0x9FFF;

            static IEnumerable<INode> Descendants(INode n)
            {
                foreach (var c in n.ChildNodes)
                {
                    yield return c;
                    foreach (var d in Descendants(c)) yield return d;
                }
            }

            static IEnumerable<IElement> Ancestors(INode n)
            {
                for (var p = n.ParentElement; p is not null; p = p.ParentElement) yield return p;
            }
        }
    }
}
