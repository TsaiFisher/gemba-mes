using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>把一份使用手冊 HTML 解析成雙語 chunk（規格 §10.2）。</summary>
public interface IManualDocParser
{
    /// <summary>
    /// 解析單一手冊頁，回傳中英兩組 chunk。
    /// </summary>
    /// <param name="pageUrlDir">該頁所在的網站目錄，如 <c>/docs/user/ord</c>（用來把相對圖片路徑解析成絕對路徑）。</param>
    /// <param name="html">整份 HTML 原文（中英同頁並存）。</param>
    /// <param name="assetExists">
    /// 給定網站絕對路徑，回答該靜態檔是否真的存在。**必填、無預設值**——
    /// 若預設成「一律存在」，英文截圖缺漏時會靜默存進一個不存在的路徑，
    /// 而破圖不會出現在任何日誌裡。
    /// </param>
    IReadOnlyList<ParsedChunk> Parse(string pageUrlDir, string html, Func<string, bool> assetExists);

    /// <summary>
    /// 同 <see cref="Parse"/>，但**不套用短頁合併門檻**，且每一塊都帶 <c>Anchor</c>。
    ///
    /// <para>給手冊搜尋用。兩者的粒度需求相反：Q&amp;A 要大塊（一塊就是餵給模型的上下文，
    /// 切碎了「點『查詢』」會失去它屬於哪個畫面的線索），搜尋要小塊（一塊就是一個跳轉目標，
    /// 併成整頁等於把使用者丟在頁首）。實測 75% 的頁面低於門檻——
    /// 讓搜尋沿用 <see cref="Parse"/> 的結果，等於四分之三的手冊都沒有段落定位。</para>
    /// </summary>
    IReadOnlyList<ParsedChunk> ParseSections(string pageUrlDir, string html, Func<string, bool> assetExists);
}

/// <summary>
/// AngleSharp 實作。
///
/// <para><b>三個非做不可的細節，每一個做錯都不會報錯：</b></para>
/// <list type="number">
///   <item>
///     <b>先依 <c>lang</c> 拆語系，再切塊</b>（§10.2-0）。手冊中英文在同一份 HTML 裡並存，
///     <c>display:none</c> 只是 CSS 視覺效果，對 <c>TextContent</c> 完全沒有作用。直接抽字會得到
///     「交期達成率Delivery Rate 路由：/DeliveryRate…Route: /DeliveryRate…」這種逐句交錯的字串——
///     embedding 被稀釋、詞彙索引混入兩種語言、引文貼給使用者是亂的，三者都不會拋例外。
///   </item>
///   <item>
///     <b>用文件順序遞迴走訪，不能只走 <c>main</c> 的直屬子元素。</b>實測 134 頁共 492 個
///     <c>&lt;h2&gt;</c>，其中 <c>sys/index.html</c> 有 1 個包在 <c>&lt;div&gt;</c> 裡。
///     只取直屬子元素會少切一個區段，而那一段的內容會被併進前一塊——頁數不變、總字數不變，
///     沒有任何指標會顯示異常。
///   </item>
///   <item>
///     <b>相鄰元素之間必須補分隔。</b><c>TextContent</c> 會把
///     <c>&lt;td&gt;出貨日&lt;/td&gt;&lt;td&gt;納入統計的區間&lt;/td&gt;</c> 黏成「出貨日納入統計的區間」，
///     於是雙字切詞會產生「日納」這種橫跨兩格、實際上不存在的詞。283 張欄位說明表全部受影響。
///   </item>
/// </list>
/// </summary>
public class ManualDocParser : IManualDocParser
{
    /// <summary>
    /// 低於這個字數的頁面整頁作為單一 chunk（§10.2-1）。
    ///
    /// 依實測分佈選定（134 頁 × 2 語系＝268 組，見 <c>ManualCorpusFacts.量測_切塊粒度與字數分佈</c>）：
    /// 每頁每語系字數 min=123 / p25=635 / <b>中位=1191</b> / p75=2016 / p95=3838 / max=10197。
    /// 門檻設在 2000 的結果是 <b>75% 的頁面整頁一塊</b>，只有欄位說明特別長的那些才切。
    ///
    /// 為什麼寧可整頁一塊：把一頁 800 字的畫面說明硬切成三塊，每一塊都會缺少判斷所需的上下文——
    /// 「點『查詢』」單獨看不出來是哪個畫面的查詢，而檢索命中的正是這種操作步驟。
    /// </summary>
    internal const int SinglePageMaxChars = 2000;

    private static readonly HtmlParser Html = new();

    /// <summary>會在前後產生換行的元素。目的是**分隔**，不是還原排版。</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "header", "footer", "blockquote", "pre",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "caption", "figure", "figcaption",
    };

    /// <summary>完全不進入的元素（其內容不是給人讀的）。</summary>
    private static readonly HashSet<string> SkipTags = new(StringComparer.Ordinal)
    {
        "script", "style", "template", "noscript",
    };

    public IReadOnlyList<ParsedChunk> Parse(string pageUrlDir, string html, Func<string, bool> assetExists)
        => ParseCore(pageUrlDir, html, assetExists, mergeShortPages: true);

    public IReadOnlyList<ParsedChunk> ParseSections(string pageUrlDir, string html, Func<string, bool> assetExists)
        => ParseCore(pageUrlDir, html, assetExists, mergeShortPages: false);

    private static IReadOnlyList<ParsedChunk> ParseCore(
        string pageUrlDir, string html, Func<string, bool> assetExists, bool mergeShortPages)
    {
        ArgumentNullException.ThrowIfNull(assetExists);

        var doc = Html.ParseDocument(html);

        // main.content 之外的東西（navbar、sidebar）是**每一頁都相同的整個模組畫面清單**。
        // 納進來的話，同模組每一塊 chunk 都會含有彼此的畫面名稱，檢索時整個模組的塊分不出高下——
        // 這比漏掉內容更難察覺，因為每一塊看起來都「有命中」。
        var main = doc.QuerySelector("main.content")
            ?? throw new InvalidOperationException(
                $"手冊頁缺少 <main class=\"content\">，無法界定內文範圍：{pageUrlDir}。" +
                "索引建置刻意在此中止而非略過該頁——靜默跳過會讓該畫面在 Q&A 中永久消失。");

        var result = new List<ParsedChunk>();
        foreach (var lang in new[] { "zh", "en" })
            result.AddRange(ParseOneLang(pageUrlDir, main, lang, assetExists, mergeShortPages));

        return result;
    }

    private static List<ParsedChunk> ParseOneLang(
        string pageUrlDir, IElement main, string lang, Func<string, bool> assetExists, bool mergeShortPages)
    {
        var scoped = (IElement)main.Clone(deep: true);

        // 拆語系：把「另一個語系」的 span 整棵移除。剩下的就是單一語系的完整文件。
        var other = lang == "zh" ? "en" : "zh";
        foreach (var el in scoped.QuerySelectorAll($"[lang=\"{other}\"]").ToList())
            el.Remove();

        var pageTitle = TextOf(scoped.QuerySelector("h1"));

        var state = new ChunkState(pageUrlDir, lang, pageTitle, assetExists);
        foreach (var child in scoped.ChildNodes)
            Walk(child, state);

        return state.Build(mergeShortPages);
    }

    private static void Walk(INode node, ChunkState st)
    {
        if (node is IText text)
        {
            st.AppendText(text.Data);
            return;
        }

        if (node is not IElement el) return;

        var tag = el.LocalName;

        if (SkipTags.Contains(tag)) return;

        // h1 已作為頁標題（麵包屑的第一段），不重複進內文。
        if (tag == "h1") return;

        // h2 是切點：關掉目前這塊、以它為標題開新的一塊，且**不遞迴進去**
        // （標題文字歸標題，不歸內文）。
        if (tag == "h2")
        {
            // id 是段落錨點（見 ParsedChunk.Anchor）。**取自 h2 而不是自己算**——
            // 搜尋結果的 #anchor 必須與頁面上真正存在的 id 逐字相同，
            // 兩邊各算一次就會有算法漂移的一天，而症狀是連結跳到頁首、沒有任何錯誤。
            st.StartChunk(TextOf(el), el.GetAttribute("id") ?? string.Empty);
            return;
        }

        if (tag == "img")
        {
            st.AddImage(el);
            return;
        }

        if (tag == "br")
        {
            st.LineBreak();
            return;
        }

        var isBlock = BlockTags.Contains(tag);
        var isCell = tag is "td" or "th";

        if (isBlock) st.LineBreak();
        if (isCell) st.CellSeparator();

        foreach (var child in el.ChildNodes) Walk(child, st);

        if (isBlock) st.LineBreak();
    }

    /// <summary>
    /// 取單一元素的可讀文字。<c>&lt;h2&gt;</c> 內含 <c>&lt;i class="bi…"&gt;</c> 圖示元素，
    /// 用 regex 抓標籤之間的文字會得到空標題（§10.2-3 舉的實例就是 <c>ord/delivery-rate.html</c>）。
    /// </summary>
    private static string TextOf(IElement? el)
    {
        if (el == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var n in el.Descendants<INode>())
            if (n is IText t) sb.Append(t.Data);

        return Normalize(sb.ToString());
    }

    /// <summary>空白正規化：不斷行空白轉一般空白、連續空白收成一個、去除首尾。</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;

        foreach (var raw in s)
        {
            // U+00A0 是 &nbsp;：手冊拿它排版（「起 &nbsp;|&nbsp; 迄」），對檢索就是一個空白。
            var c = raw == '\u00A0' ? ' ' : raw;
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>逐塊累積文字與圖片的狀態機。</summary>
    private sealed class ChunkState
    {
        private readonly string _pageUrlDir;
        private readonly string _lang;
        private readonly string _pageTitle;
        private readonly Func<string, bool> _assetExists;

        private readonly List<(string Heading, StringBuilder Text, List<string> Images, string Anchor)> _chunks = new();

        public ChunkState(string pageUrlDir, string lang, string pageTitle, Func<string, bool> assetExists)
        {
            _pageUrlDir = pageUrlDir;
            _lang = lang;
            _pageTitle = pageTitle;
            _assetExists = assetExists;
            // 頁首到第一個 h2 之前的那一塊：沒有 h2 可錨，錨點留空（跳頁首）。
            _chunks.Add((pageTitle, new StringBuilder(), new List<string>(), string.Empty));
        }

        private (string Heading, StringBuilder Text, List<string> Images, string Anchor) Current => _chunks[^1];

        public void StartChunk(string heading, string anchor)
        {
            var path = string.IsNullOrEmpty(heading) ? _pageTitle : $"{_pageTitle} › {heading}";
            _chunks.Add((path, new StringBuilder(), new List<string>(), anchor));
        }

        public void AppendText(string s)
        {
            var t = Normalize(s);
            if (t.Length == 0) return;

            var sb = Current.Text;
            if (sb.Length > 0 && sb[^1] is not ('\n' or ' ' or '|')) sb.Append(' ');
            sb.Append(t);
        }

        public void LineBreak()
        {
            var sb = Current.Text;
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        }

        /// <summary>表格欄位之間的分隔。避免「出貨日納入統計的區間」黏成一個假詞。</summary>
        public void CellSeparator()
        {
            var sb = Current.Text;
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append(" | ");
        }

        public void AddImage(IElement img)
        {
            var src = img.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src)) return;

            var resolved = Resolve(src);

            if (_lang == "en")
            {
                var en = img.GetAttribute("data-src-en");
                if (!string.IsNullOrWhiteSpace(en))
                {
                    var enResolved = Resolve(en);
                    // 英文截圖允許缺漏，頁面本身靠 onerror 退回中文圖；索引也要做同一層 fallback。
                    resolved = _assetExists(enResolved) ? enResolved : resolved;
                }
            }

            if (!Current.Images.Contains(resolved)) Current.Images.Add(resolved);
        }

        private string Resolve(string src) =>
            src.StartsWith('/') ? src : $"{_pageUrlDir.TrimEnd('/')}/{src}";

        public List<ParsedChunk> Build(bool mergeShortPages)
        {
            var built = _chunks
                .Select(c => (c.Heading, Text: Tidy(c.Text.ToString()), c.Images, c.Anchor))
                .Where(c => c.Text.Length > 0 || c.Images.Count > 0)
                .ToList();

            // 整頁字數低於門檻 → 併回單一 chunk（§10.2-1）。
            // 圖片與各段標題都要保留：標題含專有名詞，是詞彙檢索的主要命中來源。
            if (mergeShortPages && built.Sum(c => c.Text.Length) < SinglePageMaxChars && built.Count > 1)
            {
                var merged = string.Join("\n", built.Select(c =>
                    c.Heading == _pageTitle ? c.Text : $"{HeadingLeaf(c.Heading)}\n{c.Text}"));

                return new List<ParsedChunk>
                {
                    new(_lang, 0, _pageTitle, merged,
                        built.SelectMany(c => c.Images).Distinct().ToList(), string.Empty),
                };
            }

            return built
                .Select((c, i) => new ParsedChunk(_lang, i, c.Heading, c.Text, c.Images, c.Anchor))
                .ToList();
        }

        private static string HeadingLeaf(string headingPath)
        {
            var idx = headingPath.LastIndexOf('›');
            return idx < 0 ? headingPath : headingPath[(idx + 1)..].Trim();
        }

        /// <summary>收掉空行與行首尾空白，讓 chunk 內容是可直接貼進 prompt 的乾淨文字。</summary>
        private static string Tidy(string s)
        {
            var lines = s.Split('\n')
                .Select(l => l.Trim().Trim('|').Trim())
                .Where(l => l.Length > 0);

            return string.Join("\n", lines);
        }
    }
}
