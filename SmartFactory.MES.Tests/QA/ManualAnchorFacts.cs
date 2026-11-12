using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SmartFactory.MES.Modules.QA.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 手冊**段落錨點**的守門與補齊工具。
    ///
    /// <para>搜尋結果要落在段落而不是頁首，靠的是每個 <c>&lt;h2&gt;</c> 都有 <c>id</c>——
    /// <c>ManualDocParser</c> 的切點就是 h2，一塊 chunk 對應一個 h2 區段。
    /// 少一個 id，那一塊的搜尋結果就退化成「跳到該頁最上方」，
    /// 使用者落在一頁三千字的開頭自己找，而**這件事不會有任何錯誤**。</para>
    ///
    /// <para><b>守門與補齊工具刻意寫在同一個檔、共用同一份 <see cref="Slug"/>。</b>
    /// 拆成兩份的話，產生規則與驗證規則會各自演化，最後補出來的 id 通不過自己的守門，
    /// 或更糟——守門放行了一批工具永遠產不出來的 id。</para>
    ///
    /// <para><b>補齊（會改寫手冊原始檔）只在環境變數開啟時執行：</b></para>
    /// <code>MANUAL_ANCHOR_REGEN=1 dotnet test --filter ManualAnchorFacts</code>
    /// </summary>
    public class ManualAnchorFacts
    {
        private const string RegenEnvVar = "MANUAL_ANCHOR_REGEN";

        /// <summary>
        /// id 長度上限。網址要能貼進聊天視窗而不被折行截斷，
        /// 且 <c>Field reference for the outsourcing receipt</c> 這種長標題
        /// 產出的 id 在網址列比頁面本身還長。截斷一律切在 <c>-</c> 邊界，不切半個字。
        /// </summary>
        private const int MaxSlugLength = 40;

        private readonly ITestOutputHelper _out;

        public ManualAnchorFacts(ITestOutputHelper output) => _out = output;

        /// <summary>收錄範圍內的手冊頁（與索引產生器同一份判斷，見 <see cref="ManualSearchScope"/>）。</summary>
        internal static IEnumerable<string> SearchablePages()
        {
            var root = ManualCorpusFacts.ManualRoot();
            foreach (var file in Directory.EnumerateFiles(root, "*.html", SearchOption.AllDirectories).OrderBy(p => p))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ManualSearchScope.Includes(rel)) yield return file;
            }
        }

        /// <summary>
        /// 由標題文字產生錨點 id。
        ///
        /// <para><b>取英文標題，不取中文。</b>中英文在同一份 HTML 裡並存、共用同一個
        /// <c>id</c>，中文 slug 會讓英文讀者拿到一個 URL-encode 過的亂碼網址；
        /// 而純序號（<c>sec-3</c>）的問題相反——中間插一節，後面每一節的 id 全部位移，
        /// 舊連結靜默指到隔壁段落。英文標題缺漏時才退回序號。</para>
        /// </summary>
        internal static string Slug(string englishHeading, int ordinal)
        {
            var sb = new StringBuilder(englishHeading.Length);
            foreach (var c in englishHeading.ToLowerInvariant())
                sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');

            var collapsed = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');

            if (collapsed.Length > MaxSlugLength)
            {
                collapsed = collapsed[..MaxSlugLength];
                var lastDash = collapsed.LastIndexOf('-');
                if (lastDash > 0) collapsed = collapsed[..lastDash];
            }

            // 數字開頭的 id 在 CSS 選擇器裡需要跳脫，而頁內高亮正是用 querySelector 找它。
            return collapsed.Length == 0 || char.IsDigit(collapsed[0])
                ? $"section-{ordinal}"
                : collapsed;
        }

        /// <summary>h2 的英文標題文字；沒有 <c>[lang=en]</c> 子節點時回空字串。</summary>
        private static string EnglishHeadingOf(IElement h2)
        {
            var sb = new StringBuilder();
            foreach (var el in h2.QuerySelectorAll("[lang=\"en\"]")) sb.Append(el.TextContent).Append(' ');
            return sb.ToString().Trim();
        }

        /// <summary>一頁之內，依序算出每個 h2 應有的 id（已有 id 者原樣保留）。</summary>
        private static List<string> PlanAnchors(IHtmlParser parser, string html, string relPath)
        {
            var doc = parser.ParseDocument(html);
            var main = doc.QuerySelector("main.content")
                ?? throw new InvalidOperationException($"{relPath} 缺少 <main class=\"content\">");

            var used = new HashSet<string>(StringComparer.Ordinal);
            var planned = new List<string>();
            var ordinal = 0;

            foreach (var h2 in main.QuerySelectorAll("h2"))
            {
                ordinal++;

                // 既有 id 一律不動：index 頁的側邊欄與跨頁連結已經指著它們（#flow 就有 71 處）。
                var existing = h2.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    used.Add(existing);
                    planned.Add(existing);
                    continue;
                }

                var candidate = Slug(EnglishHeadingOf(h2), ordinal);
                var unique = candidate;
                var suffix = 2;
                while (!used.Add(unique)) unique = $"{candidate}-{suffix++}";

                planned.Add(unique);
            }

            return planned;
        }

        // ── 守門 ─────────────────────────────────────────────────────────

        [Fact]
        public void 每個h2都有段落錨點且頁內唯一()
        {
            var parser = new HtmlParser();
            var violations = new List<string>();
            var pages = 0;
            var headings = 0;

            foreach (var file in SearchablePages())
            {
                pages++;
                var rel = Path.GetRelativePath(ManualCorpusFacts.ManualRoot(), file).Replace('\\', '/');
                var doc = parser.ParseDocument(File.ReadAllText(file));
                var main = doc.QuerySelector("main.content");
                Assert.True(main != null, $"{rel} 缺少 <main class=\"content\">");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var ordinal = 0;

                foreach (var h2 in main!.QuerySelectorAll("h2"))
                {
                    ordinal++;
                    headings++;
                    var id = h2.GetAttribute("id");

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        violations.Add($"{rel} 第 {ordinal} 個 h2「{h2.TextContent.Trim()}」沒有 id");
                        continue;
                    }

                    if (!seen.Add(id))
                        violations.Add($"{rel} 的 id=\"{id}\" 在同一頁重複——瀏覽器只會跳到第一個");

                    if (id.Any(c => c > 127))
                        violations.Add($"{rel} 的 id=\"{id}\" 含非 ASCII 字元，網址會被編碼成亂碼");
                }
            }

            // 「規則形同關閉」的防呆：路徑解析一錯，上面的迴圈一頁都不跑而測試照樣綠。
            Assert.True(pages >= ManualSearchScope.MinimumPages,
                $"只掃到 {pages} 頁手冊，少於下限 {ManualSearchScope.MinimumPages}——手冊目錄結構可能已變，規則形同關閉");
            Assert.True(headings > 500,
                $"只掃到 {headings} 個 h2，遠少於預期（2026-09-22 實測 663）——切點或頁面結構已變");

            _out.WriteLine($"掃描 {pages} 頁、{headings} 個 h2");

            Assert.True(violations.Count == 0,
                $"手冊段落錨點有 {violations.Count} 處問題——" +
                $"執行 `{RegenEnvVar}=1 dotnet test --filter ManualAnchorFacts` 補齊：\n  " +
                string.Join("\n  ", violations.Take(30)));
        }

        // ── 補齊工具（預設不執行） ───────────────────────────────────────

        [Fact]
        public void 補齊缺漏的段落錨點()
        {
            if (Environment.GetEnvironmentVariable(RegenEnvVar) != "1")
            {
                _out.WriteLine($"未設 {RegenEnvVar}=1，略過補齊（這是預設行為，不是失敗）。");
                return;
            }

            var parser = new HtmlParser();
            var root = ManualCorpusFacts.ManualRoot();
            var changedFiles = 0;
            var addedIds = 0;

            foreach (var file in SearchablePages())
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var html = File.ReadAllText(file);
                var planned = PlanAnchors(parser, html, rel);

                // 就地插入，**不用 AngleSharp 重新序列化整份文件**——那會連帶改寫
                // 自閉合標籤與屬性引號，171 頁的 diff 會從「每頁幾行」變成「每頁全部」，
                // 沒有人能在那種 diff 裡看出實際改了什麼。
                var openTags = Regex.Matches(html, "<h2(?![a-zA-Z0-9])[^>]*>");
                Assert.True(openTags.Count == planned.Count,
                    $"{rel}：正則找到 {openTags.Count} 個 h2 開標籤，但文件樹有 {planned.Count} 個。" +
                    "兩者對不上就無法保證插對位置，此頁需人工處理。");

                var sb = new StringBuilder(html.Length + planned.Count * 20);
                var cursor = 0;
                var fileChanged = false;

                for (var i = 0; i < openTags.Count; i++)
                {
                    var tag = openTags[i];
                    if (tag.Value.Contains(" id=", StringComparison.Ordinal)) continue;

                    sb.Append(html, cursor, tag.Index + 3 - cursor);   // "<h2" 之後
                    sb.Append(" id=\"").Append(planned[i]).Append('"');
                    cursor = tag.Index + 3;
                    fileChanged = true;
                    addedIds++;
                }

                if (!fileChanged) continue;

                sb.Append(html, cursor, html.Length - cursor);
                File.WriteAllText(file, sb.ToString());
                changedFiles++;
            }

            _out.WriteLine($"補齊完成：{changedFiles} 個檔案、新增 {addedIds} 個 id。");
        }
    }
}
