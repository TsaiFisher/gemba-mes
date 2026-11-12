using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 教學系統的**素材盤點**：課程（模組目錄頁的流程圖）、步驟（<c>.step-row</c>）、可高亮性。
    ///
    /// <para>全部是量測，一條門檻都不設——本檔的用途是讓「這個功能該不該做、要怎麼做」
    /// 有數字可依據。把首測值凍結成回歸門檻是反模式（同
    /// <see cref="ManualCorpusFacts"/> 與 <c>ManualHardBenchmarkTests</c> 的立場）。</para>
    ///
    /// <para><b>為什麼教學系統不需要另寫教材：</b>流程圖的每一條 <c>.flow-row</c> 就是一條作業程序
    /// （<c>.flow-row-label</c> 是課名、<c>.flow-steps</c> 的節點序列是站別），
    /// 而每一站的手冊頁裡的 <c>.step-row</c> 就是該站的逐步驟說明。兩者都是雙語的。
    /// 教學系統做的是在執行期把這些組起來，而不是產生第三份會漂移的副本。</para>
    /// </summary>
    public class TutorCourseFacts
    {
        private readonly ITestOutputHelper _out;
        private static readonly HtmlParser Html = new();

        public TutorCourseFacts(ITestOutputHelper output) => _out = output;

        // ── 資料模型 ─────────────────────────────────────────────────────

        /// <summary>流程圖上的一個站。<paramref name="Kind"/> 是序列文法的 token 種類，不只是節點。</summary>
        internal sealed record Stop(string Kind, string NameZh, string Module, string? Href);

        internal sealed record Course(
            string Page, int RowIndex, string? Id, string LabelZh, bool Alt, IReadOnlyList<Stop> Stops);

        /// <summary>
        /// 解析 <c>flow-steps</c> 的**直接子元素序列**，而不是 <c>querySelectorAll(".flow-node")</c>。
        ///
        /// <para>後者會把 <c>.flow-plus</c>（擇一，全站 56 處）與 <c>.flow-cond</c>（分支條件）整個吃掉，
        /// 於是「進貨單｜委外收貨｜完工入庫｜銷退單 → 上架入庫」會被讀成四站依序進行——
        /// 教出一條系統裡不存在的流程，而且沒有任何訊號。</para>
        /// </summary>
        internal static List<Course> AllCourses()
        {
            var courses = new List<Course>();

            foreach (var file in ManualCorpusFacts.AllPages())
            {
                var doc = Html.ParseDocument(File.ReadAllText(file));
                var rows = doc.QuerySelectorAll("div.flow-row");
                // 手冊首頁在 docs/user/ 底下，GetFileName(GetDirectoryName()) 會回 "user"——
                // 那是手冊根目錄本身、不是模組目錄。用路徑比對而不是「取最後一段目錄名」。
                var parent = Path.GetDirectoryName(file) ?? "";
                var dir = Path.GetFullPath(parent) == Path.GetFullPath(ManualCorpusFacts.ManualRoot())
                    ? ""
                    : Path.GetFileName(parent);

                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    var steps = row.QuerySelector(".flow-steps");
                    if (steps is null) continue;

                    var stops = new List<Stop>();
                    foreach (var child in steps.Children)
                    {
                        var cls = child.ClassName ?? "";
                        if (cls.Contains("flow-arrow", StringComparison.Ordinal)) continue;

                        var kind =
                            cls.Contains("flow-plus", StringComparison.Ordinal) ? "plus" :
                            cls.Contains("flow-cond", StringComparison.Ordinal) ? "cond" :
                            cls.Contains("evt", StringComparison.Ordinal) ? "evt" :
                            cls.Contains("ext", StringComparison.Ordinal) ? "ext" : "node";

                        stops.Add(new Stop(
                            kind,
                            Zh(child.QuerySelector(".flow-name") ?? child),
                            Zh(child.QuerySelector(".flow-mod")),
                            child.GetAttribute("href")));
                    }

                    courses.Add(new Course(
                        Page: dir.Length == 0 ? "(root)" : dir,
                        RowIndex: i,
                        Id: row.GetAttribute("id"),
                        LabelZh: Zh(row.QuerySelector(".flow-row-label")),
                        Alt: cls_alt(row),
                        Stops: stops));
                }
            }

            return courses;

            static bool cls_alt(IElement r) => (r.ClassName ?? "").Contains("alt", StringComparison.Ordinal);
        }

        /// <summary>
        /// 取中文字面：依 <c>doc-i18n.css</c> 的顯示語意「除了 <c>[lang=en]</c> 以外全都保留」逐節點走訪。
        ///
        /// <para>不能只抓第一個 <c>[lang=zh]</c>——手冊的一句話常被 <c>&lt;b&gt;</c>／<c>&lt;a&gt;</c>
        /// 切成好幾段 zh／en 交錯的 span，只取第一段會截斷成半句
        /// （mrp-run.html 的「系統會依該料」原本就是這樣被截掉的）。教學系統要拿這段字做旁白，
        /// 截半句就是唸半句。</para>
        ///
        /// <para>不在任何 lang span 內的裸文字（數字、標點、空白）兩種語系都要顯示，故一律保留。</para>
        /// </summary>
        private static string Zh(IElement? el)
        {
            if (el is null) return "";
            var sb = new StringBuilder();
            Walk(el, sb);
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

            static void Walk(INode node, StringBuilder sb)
            {
                foreach (var child in node.ChildNodes)
                {
                    if (child is IText t) { sb.Append(t.Data); continue; }
                    if (child is IElement e
                        && !"en".Equals(e.GetAttribute("lang"), StringComparison.OrdinalIgnoreCase))
                        Walk(e, sb);
                }
            }
        }

        /// <summary>把節點的 href 換算成手冊檔案的實體路徑；指不到檔案就回 null。</summary>
        internal static string? ResolvePage(Course c, Stop s)
        {
            if (string.IsNullOrEmpty(s.Href)) return null;
            var baseDir = c.Page == "(root)"
                ? ManualCorpusFacts.ManualRoot()
                : Path.Combine(ManualCorpusFacts.ManualRoot(), c.Page);
            var full = Path.GetFullPath(Path.Combine(baseDir, s.Href.Split('#')[0]));
            return File.Exists(full) ? full : null;
        }

        /// <summary>一頁裡 <c>main.content</c> 內的雙語步驟，依**文件順序**收集。</summary>
        internal static List<(string Section, string TextZh)> StepsOf(string file)
        {
            var main = Html.ParseDocument(File.ReadAllText(file)).QuerySelector("main.content");
            if (main is null) return new();

            var section = "";
            var list = new List<(string, string)>();

            foreach (var el in main.QuerySelectorAll("h2, h3, .step-row"))
            {
                if (el.TagName is "H2" or "H3") { section = Zh(el); continue; }
                list.Add((section, Zh(el.QuerySelector("div") ?? el)));
            }

            return list;
        }

        // ── 量測 ─────────────────────────────────────────────────────────

        [Fact]
        public void 量測_課程盤點()
        {
            var courses = AllCourses();
            var stops = courses.SelectMany(c => c.Stops).ToList();

            _out.WriteLine($"課程（.flow-row）＝{courses.Count} 條，其中替代路徑（.alt）＝{courses.Count(c => c.Alt)} 條");
            var ids = courses.Select(c => c.Id ?? "").Where(s => s != "").ToList();
            _out.WriteLine($"已有 id 的課程＝{ids.Count} 條，全手冊相異 id＝{ids.Distinct().Count()} 個"
                         + "  ← 兩數要相等且等於課程數，否則教學 URL 會指到兩條課程");
            _out.WriteLine($"流程圖分布於 {courses.Select(c => c.Page).Distinct().Count()} 個模組目錄頁");
            _out.WriteLine("");

            foreach (var g in stops.GroupBy(s => s.Kind).OrderByDescending(g => g.Count()))
                _out.WriteLine($"  token {g.Key,-5} ＝ {g.Count(),4}");

            var nodes = stops.Where(s => s.Kind is "node" or "ext" or "evt").ToList();
            var playable = nodes.Where(s => !string.IsNullOrEmpty(s.Href)).ToList();
            _out.WriteLine($"\n節點合計＝{nodes.Count}，其中有 href（有畫面可演示）＝{playable.Count}，"
                         + $"無 href（外部事件）＝{nodes.Count - playable.Count}");

            var sizes = courses.Select(c => c.Stops.Count(s => s.Kind is "node" or "ext" or "evt")).OrderBy(x => x).ToList();
            _out.WriteLine($"每條課程站數  min={sizes[0]} 中位={sizes[sizes.Count / 2]} max={sizes[^1]}");
            _out.WriteLine($"含「擇一」的課程＝{courses.Count(c => c.Stops.Any(s => s.Kind == "plus"))} 條"
                         + $"、含分支條件＝{courses.Count(c => c.Stops.Any(s => s.Kind == "cond"))} 條");

            Assert.NotEmpty(courses);

            // 這不是把首測值凍成門檻，是不變式：課程 id 是教學系統的定址方式，
            // 少一個或撞一個都會讓某條課程永遠打不開（或打開錯的那條）。
            Assert.Equal(courses.Count, ids.Count);
            Assert.Equal(courses.Count, ids.Distinct().Count());
        }

        [Fact]
        public void 量測_每站的步驟數與截圖數()
        {
            var courses = AllCourses();
            var perStop = new List<int>();
            var noScreenshot = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var c in courses)
            {
                foreach (var s in c.Stops)
                {
                    var page = ResolvePage(c, s);
                    if (page is null || !seen.Add(page)) continue;

                    var steps = StepsOf(page).Count;
                    perStop.Add(steps);

                    var imgs = Html.ParseDocument(File.ReadAllText(page))
                        .QuerySelectorAll("main.content img").Length;
                    if (imgs == 0) noScreenshot.Add($"{Path.GetFileName(page)}（{steps} 步驟）");
                }
            }

            var o = perStop.OrderBy(x => x).ToList();
            _out.WriteLine($"被課程指到的相異手冊頁＝{seen.Count}");
            _out.WriteLine($"每頁步驟數  min={o[0]} 中位={o[o.Count / 2]} max={o[^1]} 總計={o.Sum()}");
            _out.WriteLine($"完全沒有截圖的頁＝{noScreenshot.Count}");
            foreach (var n in noScreenshot.Take(15)) _out.WriteLine($"    {n}");

            Assert.NotEmpty(perStop);
        }

        /// <summary>
        /// <b>這是「要不要做」的閘門。</b>決策 1（截圖模擬演示）的價值取決於
        /// 「步驟指得到截圖上某個控制項」的比例；比例太低的話，做出來就是
        /// 同一張圖播四次配字幕，那應該回頭改演示形式而不是硬做完。
        ///
        /// <para>這裡只印出 pilot 的每一個步驟與兩個**機械代理指標**，不做自動判定——
        /// 「這句話指的東西在不在列表截圖上」是語意判斷，24 個步驟用眼睛看比任何關鍵字規則可靠。</para>
        /// </summary>
        [Fact]
        public void 量測_pilot採購主流程的可高亮性()
        {
            var pilot = AllCourses().Single(c => c.LabelZh == "採購主流程");

            _out.WriteLine($"課程「{pilot.LabelZh}」＠{pilot.Page}/index.html 第 {pilot.RowIndex} 條");
            _out.WriteLine(string.Join(" ", pilot.Stops.Select(s =>
                s.Kind switch { "plus" => "｜", "cond" => $"[{s.NameZh}]", _ => $"{s.NameZh}({s.Kind})" })));
            _out.WriteLine("");

            var total = 0;
            var quoted = 0;

            foreach (var s in pilot.Stops)
            {
                var page = ResolvePage(pilot, s);
                if (page is null)
                {
                    _out.WriteLine($"── {s.NameZh}（{s.Kind}）：無畫面，教學播「外部事件卡」");
                    continue;
                }

                var steps = StepsOf(page);
                var imgs = Html.ParseDocument(File.ReadAllText(page))
                    .QuerySelectorAll("main.content img").Length;
                _out.WriteLine($"── {s.NameZh}　{Path.GetFileName(page)}　步驟 {steps.Count}　截圖 {imgs}");

                foreach (var (section, text) in steps)
                {
                    total++;
                    // 代理指標一：有「」括住的控制項名稱＝指向一個具體控制項。
                    var hasLabel = text.Contains('「', StringComparison.Ordinal);
                    if (hasLabel) quoted++;
                    // 代理指標二：提到側欄/面板/明細行＝動作發生在 offcanvas 內，不在列表截圖上。
                    var inPanel = text.Contains("側欄") || text.Contains("面板") || text.Contains("明細行");

                    _out.WriteLine($"     [{(hasLabel ? "標" : " ")}{(inPanel ? "欄" : " ")}] ({section}) {text}");
                }
            }

            _out.WriteLine($"\npilot 步驟合計＝{total}，其中有具名控制項（「」）＝{quoted}");
            _out.WriteLine("「標」＝指向具名控制項；「欄」＝動作在側欄內，列表截圖上看不到。");
            _out.WriteLine("實際可標熱點數需人工判讀上表，這兩個欄位只是縮小判讀範圍。");

            Assert.True(total > 0);
        }
    }
}
