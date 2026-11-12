using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 對**真實手冊語料**（134 頁）的量測與全域守門，而非人造 HTML 的單元測試。
    ///
    /// 分成兩件事：
    /// <list type="bullet">
    ///   <item><b>守門</b>——每一頁都要能解析、雙語都要有內容、不得有語言滲漏。
    ///     這幾條是 S1 唯一能自動抓到「手冊改版後索引壞掉」的地方（缺口 G2 的部分解）。</item>
    ///   <item><b>量測</b>——切塊粒度與字數分佈。門檻值必須由實測決定，
    ///     憑感覺設一個數字然後在註解裡寫「依實測」是最糟的一種文件。</item>
    /// </list>
    /// </summary>
    public class ManualCorpusFacts
    {
        private readonly ITestOutputHelper _out;
        private readonly ManualDocParser _parser = new();

        public ManualCorpusFacts(ITestOutputHelper output) => _out = output;

        internal static string ManualRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "wwwroot", "docs", "user");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("找不到 wwwroot/docs/user——測試需要真實手冊語料");
        }

        internal static IEnumerable<string> AllPages() =>
            Directory.EnumerateFiles(ManualRoot(), "*.html", SearchOption.AllDirectories).OrderBy(p => p);

        /// <summary>把實體路徑換算成該頁的網站目錄（/docs/user/ord）。</summary>
        internal static string UrlDirOf(string file)
        {
            var rel = Path.GetRelativePath(ManualRoot(), file).Replace('\\', '/');
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
            return dir.Length == 0 ? "/docs/user" : $"/docs/user/{dir}";
        }

        /// <summary>真實檔案系統的存在性探測——英文截圖 fallback 要靠它。</summary>
        internal static bool AssetExists(string webPath)
        {
            const string prefix = "/docs/user/";
            if (!webPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return File.Exists(Path.Combine(ManualRoot(), webPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
        }

        // ── 守門 ─────────────────────────────────────────────────────────

        [Fact]
        public void 每一頁都能解析且中英各自有內容()
        {
            var broken = new List<string>();

            foreach (var file in AllPages())
            {
                var chunks = _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists);

                var zh = chunks.Where(c => c.Lang == "zh").ToList();
                var en = chunks.Where(c => c.Lang == "en").ToList();

                if (zh.Count == 0 || en.Count == 0 ||
                    zh.All(c => c.Text.Length == 0) || en.All(c => c.Text.Length == 0))
                {
                    broken.Add($"{Path.GetFileName(file)} zh={zh.Count} en={en.Count}");
                }
            }

            Assert.True(broken.Count == 0,
                "以下手冊頁解析後有語系是空的（雙語拆分壞掉時就是這個形狀）：\n" + string.Join("\n", broken));
        }

        [Fact]
        public void 英文塊不得以中文為主_中文塊必須有漢字()
        {
            // §10.2-0 的核心守門。語言滲漏不會拋例外，只會讓 embedding 被稀釋、引文變成兩種語言交錯。
            //
            // 門檻是**比例**而非零容忍：英文內文合法地會引用中文字面（使用手冊首頁的
            // 「choose 繁體中文 or English」就是），而真正的滲漏是整句中文混進來，佔比會在 30% 以上。
            // 拆分若壞掉，這個數字是 50% 上下，離 10% 很遠——門檻放在中間不會兩頭不到岸。
            var leaks = new List<string>();

            foreach (var file in AllPages())
            {
                foreach (var c in _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists))
                {
                    if (c.Text.Length == 0) continue;
                    var cjkRatio = c.Text.Count(IsCjk) * 1.0 / c.Text.Length;

                    if (c.Lang == "en" && cjkRatio > 0.10)
                        leaks.Add($"{Path.GetFileName(file)} #{c.ChunkIndex} 英文塊漢字佔 {cjkRatio:P0}：「{Excerpt(c.Text)}」");

                    // 反向：中文塊完全沒有漢字，代表 [lang="en"] 被誤刪或 zh span 整段不見。
                    if (c.Lang == "zh" && cjkRatio == 0 && c.Text.Length > 40)
                        leaks.Add($"{Path.GetFileName(file)} #{c.ChunkIndex} 中文塊卻無漢字：「{Excerpt(c.Text)}」");
                }
            }

            Assert.True(leaks.Count == 0, "語言滲漏：\n" + string.Join("\n", leaks.Take(20)));
        }

        [Fact]
        public void 手冊內文的漢字全部包在lang標記內()
        {
            // 這條守的是**手冊原始碼**而不是解析器：內文若有漢字漏在 lang span 之外，
            // 它會同時出現在中文版與英文版——英文使用者今天在瀏覽器裡就看得到中文，
            // 而 RAG 只是把這個既有缺陷放大。
            //
            // 實例（本次由此測試揪出並修復）：qcr/spc-analysis.html 的
            //   <span lang="zh">…Cpk</span><span lang="en">…Cpk</span> < 1.33 建議列入改善。
            // 尾巴的「< 1.33 建議列入改善。」漏在兩個 span 之外，且 `<` 未跳脫。
            var html = new HtmlParser();
            var offenders = new List<string>();

            foreach (var file in AllPages())
            {
                var main = html.ParseDocument(File.ReadAllText(file)).QuerySelector("main.content")!;

                foreach (var node in main.Descendants<INode>())
                {
                    if (node is not IText t || !t.Data.Any(IsCjk)) continue;

                    var wrapped = false;
                    for (var p = t.ParentElement; p != null && p != main.Parent; p = p.ParentElement)
                        if (!string.IsNullOrEmpty(p.GetAttribute("lang"))) { wrapped = true; break; }

                    if (!wrapped)
                        offenders.Add($"{Path.GetFileName(file)}：「{Excerpt(t.Data.Trim())}」");
                }
            }

            Assert.True(offenders.Count == 0,
                "以下漢字沒有包在 <span lang=\"zh\"> 內，會同時顯示在中英文兩版：\n"
                + string.Join("\n", offenders));
        }

        private static bool IsCjk(char c) => c >= '一' && c <= '鿿';

        [Fact]
        public void 表格欄位之間有分隔而不黏成假詞()
        {
            // 283 張欄位說明表。TextContent 會把相鄰兩格黏起來，雙字切詞就產生橫跨兩格的假詞。
            var file = AllPages().First(p => p.EndsWith("delivery-rate.html", StringComparison.Ordinal));
            var chunks = _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists);
            var zhText = string.Join("\n", chunks.Where(c => c.Lang == "zh").Select(c => c.Text));

            Assert.Contains("客戶代號 | 只看單一客戶", zhText);
            Assert.DoesNotContain("客戶代號只看單一客戶", zhText);
        }

        [Fact]
        public void 英文塊的截圖走英文版路徑()
        {
            var file = AllPages().First(p => p.EndsWith("delivery-rate.html", StringComparison.Ordinal));
            var chunks = _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists);

            var zhImgs = chunks.Where(c => c.Lang == "zh").SelectMany(c => c.ImagePaths).ToList();
            var enImgs = chunks.Where(c => c.Lang == "en").SelectMany(c => c.ImagePaths).ToList();

            Assert.Contains("/docs/user/ord/img/delivery-rate.png", zhImgs);
            Assert.Contains("/docs/user/ord/img/en/delivery-rate.png", enImgs);
        }

        [Fact]
        public void 索引的截圖路徑全部真的存在()
        {
            // 存一個不存在的路徑，答覆會出現破圖——而破圖不會進任何日誌。
            var missing = new List<string>();

            foreach (var file in AllPages())
                foreach (var c in _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists))
                    foreach (var img in c.ImagePaths)
                        if (!AssetExists(img)) missing.Add($"{Path.GetFileName(file)} [{c.Lang}] {img}");

            Assert.True(missing.Count == 0, "索引到不存在的截圖：\n" + string.Join("\n", missing.Take(20)));
        }

        [Fact]
        public void 側欄與導覽列不進索引()
        {
            // sidebar 是整個模組的畫面清單，每頁都一樣。納進來的話同模組每塊 chunk 都彼此包含，
            // 檢索時分不出高下——而且每一塊看起來都「有命中」，比漏掉更難察覺。
            var file = AllPages().First(p => p.EndsWith("delivery-rate.html", StringComparison.Ordinal));
            var text = string.Join("\n", _parser
                .Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists)
                .Where(c => c.Lang == "zh").Select(c => c.Text));

            Assert.DoesNotContain("使用手冊首頁", text);   // navbar
            Assert.DoesNotContain("銷售預測", text);       // sidebar 上的其他畫面
        }

        // ── 量測（門檻值的依據） ─────────────────────────────────────────

        [Fact]
        public void 量測_切塊粒度與字數分佈()
        {
            var pages = 0;
            var perPageChars = new List<int>();
            var chunkChars = new List<int>();
            var chunksPerPageLang = new List<int>();

            foreach (var file in AllPages())
            {
                pages++;
                var chunks = _parser.Parse(UrlDirOf(file), File.ReadAllText(file), AssetExists);

                foreach (var lang in new[] { "zh", "en" })
                {
                    var g = chunks.Where(c => c.Lang == lang).ToList();
                    chunksPerPageLang.Add(g.Count);
                    perPageChars.Add(g.Sum(c => c.Text.Length));
                    chunkChars.AddRange(g.Select(c => c.Text.Length));
                }
            }

            _out.WriteLine($"頁數={pages}  chunk 總數={chunkChars.Count}（中英合計）");
            _out.WriteLine($"每頁每語系字數  {Stats(perPageChars)}");
            _out.WriteLine($"每個 chunk 字數 {Stats(chunkChars)}");
            _out.WriteLine($"每頁每語系塊數  {Stats(chunksPerPageLang)}");
            _out.WriteLine($"整頁一塊的比例  {chunksPerPageLang.Count(c => c == 1) * 100.0 / chunksPerPageLang.Count:F0}%");
            _out.WriteLine($"1024 維 float32 的向量體積 ≈ {chunkChars.Count * 1024 * 4 / 1024.0 / 1024.0:F1} MB");

            Assert.NotEmpty(chunkChars);
        }

        private static string Stats(List<int> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            int P(double q) => s[Math.Clamp((int)(s.Count * q), 0, s.Count - 1)];
            return $"n={s.Count} min={s[0]} p25={P(.25)} 中位={P(.5)} p75={P(.75)} p95={P(.95)} max={s[^1]} 總計={s.Sum()}";
        }

        private static string Excerpt(string s) => s.Length <= 60 ? s : s[..60] + "…";
    }
}
