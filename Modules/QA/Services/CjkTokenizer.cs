using System.Globalization;
using System.Text;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 中日韓（CJK）文字的**重疊雙字**預切詞——手冊 RAG 詞彙檢索通道的地基（規格 §10.5）。
///
/// <para><b>為什麼需要這個東西</b>（2026-08-05 對本專案 PostgreSQL 16.14 實測）：</para>
/// <code>
/// select to_tsvector('simple','銷售排名報表可依客戶或品號彙總');
/// -- → '銷售排名報表可依客戶或品號彙總':1        整句是「一個」token
/// select to_tsvector('simple','銷售排名報表…') @@ plainto_tsquery('simple','銷售');
/// -- → f
/// </code>
/// PostgreSQL 內建分詞器對 CJK 沒有斷詞能力（沒有空白可切），整句話變成單一 lexeme，
/// 只有一字不差輸入整句才命中。這個失敗的形狀是最糟的一種：**不報錯、不拋例外，
/// 只是靜默退化成純語意檢索**——而最需要精確命中的正是「銷售排名」「安全存量」這類專有名詞，
/// 恰好是混合檢索存在的理由。
///
/// <para>切成重疊雙字之後（同一台機器實測）：</para>
/// <code>
/// select to_tsvector('simple','銷售 售排 排名 名報 報表') @@ plainto_tsquery('simple','銷售'); -- → t
/// select to_tsvector('simple','銷售 售排 排名 名報 報表') @@ plainto_tsquery('simple','排名'); -- → t
/// </code>
/// 不需要安裝 <c>zhparser</c> 或 <c>pg_bigm</c>（本機亦皆未安裝，只有 <c>pg_trgm</c>）。
///
/// <para><b>索引期與查詢期必須呼叫同一個方法。</b></para>
/// 兩邊各自實作一份「看起來一樣」的切法，是這類設計的經典靜默失敗——
/// 兩份程式碼分別讀都對，只是永遠對不上，而檢索結果為空不會有任何錯誤訊息。
/// 因此本類別刻意只暴露 <see cref="Tokenize"/> 一個入口，不提供「索引版」「查詢版」的變體。
/// </summary>
public static class CjkTokenizer
{
    /// <summary>
    /// 把文字切成可直接餵給 <c>to_tsvector</c>／<c>plainto_tsquery</c> 的詞串（以空白分隔）。
    ///
    /// <list type="bullet">
    ///   <item>連續 CJK 字元 → 重疊雙字（「銷售排名」→「銷售 售排 排名」）</item>
    ///   <item>孤立的單一 CJK 字 → 原字（見 <see cref="Tokenize"/> 備註的已知限制）</item>
    ///   <item>拉丁字母／數字 → 原詞轉小寫（保留路由與代號，如 <c>/DeliveryRate</c> → <c>deliveryrate</c>）</item>
    ///   <item>其餘（標點、空白、全形符號） → 一律視為分隔符</item>
    /// </list>
    ///
    /// <para><b>已知限制</b>：單一 CJK 字的查詢（如「料」）只會命中文件中同樣孤立的單字，
    /// 不會命中「物料」裡的「料」。這是刻意的取捨——為了讓單字可查而在索引中同時放進所有單字，
    /// 會讓索引量加倍且精確度大幅下降，而真實的手冊提問不會只有一個字。</para>
    /// </summary>
    public static string Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length * 3);

        int i = 0;
        while (i < text.Length)
        {
            if (IsCjk(text[i]))
            {
                int start = i;
                while (i < text.Length && IsCjk(text[i])) i++;
                AppendBigrams(sb, text, start, i - start);
            }
            else if (IsWordChar(text[i]))
            {
                int start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                Append(sb, text.AsSpan(start, i - start), lower: true);
            }
            else
            {
                i++;   // 分隔符：標點、空白、全形符號、代理對（手冊為 zh/en，不涉及 CJK 擴充 B 區）
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把 <see cref="Tokenize"/> 的輸出組成 <c>to_tsquery</c> 用的**析取式**（<c>a | b | c</c>）。
    ///
    /// <para><b>為什麼不能直接用 <c>plainto_tsquery</c>：</b>它是 <b>AND</b> 語意。
    /// 「銷售排名怎麼看」切成「銷售 售排 排名 名怎 怎麼 麼看」之後，
    /// AND 會要求文件同時含有「名怎」「怎麼」「麼看」——那是提問的語氣詞，手冊裡不會有，
    /// 於是**每一則自然語言提問都回 0 筆**。這正是本專案第一次跑整合測試時的實際結果。</para>
    ///
    /// <para>改用 OR 之後，命中多少個詞由 <c>ts_rank</c> 換算成分數：
    /// 同時含有「銷售 售排 排名」的頁面自然排在只含「怎麼」的頁面前面。
    /// 召回交給 OR、精確度交給排序，是詞彙檢索的標準作法。</para>
    ///
    /// <para>不需要跳脫：<see cref="Tokenize"/> 只會產出字母、數字與 CJK 字元，
    /// <c>to_tsquery</c> 的運算子（<c>&amp; | ! ( ) :</c>）不可能出現在輸出中。</para>
    /// </summary>
    public static string ToOrTsQuery(string? lexemes) =>
        string.IsNullOrWhiteSpace(lexemes)
            ? string.Empty
            : string.Join(" | ", lexemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct());

    /// <summary>
    /// 給定語系回傳應搭配的 PostgreSQL 文字檢索組態。
    ///
    /// 中文走 <c>simple</c>：雙字已經在 <see cref="Tokenize"/> 切好，
    /// 這裡只需要一個**不做任何 stemming／stop word 移除**的組態，否則會把雙字再處理一次。
    /// 英文走 <c>english</c>：有空白可切，stemming 是實質加分（shipments ↔ shipment）。
    ///
    /// 因為檢索一律先以 <c>Lang</c> 硬過濾（§10.4），同一次查詢內的組態必然一致，
    /// 不會出現「用英文組態去查中文列」的混用。
    /// </summary>
    public static string ConfigFor(string? lang) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "english" : "simple";

    /// <summary>
    /// 判定提問語系（§10.4：以**提問文字**為準，UI 語系僅作判不出來時的預設）。
    ///
    /// <list type="bullet">
    ///   <item>出現任何 CJK 字元 → <c>zh</c>。不是佔比門檻：中文提問裡夾雜英文專有名詞
    ///     （「SalesRanking 怎麼查」「IQC 免驗要怎麼設」）非常常見，CJK 佔比可能低於一半，
    ///     但使用者要的顯然是中文答案；反向的情形在實務上不存在。</item>
    ///   <item>有拉丁字母、沒有 CJK → <c>en</c>。</item>
    ///   <item><b>兩者都沒有 → <c>null</c></b>（「OEE?」「2152」「？？」）。
    ///     此時**必須**由呼叫端以 UI 語系遞補；回傳一個猜的值會讓中文使用者拿到英文答案，
    ///     而且沒有任何地方看得出來那是猜的。</item>
    /// </list>
    /// </summary>
    public static string? DetectLang(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var hasLatin = false;
        foreach (var c in text)
        {
            if (IsCjk(c)) return "zh";
            if (!hasLatin && char.IsLetter(c)) hasLatin = true;
        }

        return hasLatin ? "en" : null;
    }

    private static void AppendBigrams(StringBuilder sb, string text, int start, int length)
    {
        if (length == 1)
        {
            Append(sb, text.AsSpan(start, 1), lower: false);
            return;
        }

        for (int k = 0; k < length - 1; k++)
            Append(sb, text.AsSpan(start + k, 2), lower: false);
    }

    private static void Append(StringBuilder sb, ReadOnlySpan<char> token, bool lower)
    {
        if (sb.Length > 0) sb.Append(' ');
        if (lower)
        {
            foreach (var c in token) sb.Append(char.ToLowerInvariant(c));
        }
        else
        {
            sb.Append(token);
        }
    }

    /// <summary>
    /// CJK 表意文字。涵蓋基本區、擴充 A 區與相容表意文字；
    /// 刻意**不含**全形標點（U+3000–U+303F），那些是分隔符不是詞。
    /// </summary>
    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||   // CJK Unified Ideographs
        (c >= '\u3400' && c <= '\u4DBF') ||   // Extension A
        (c >= '\uF900' && c <= '\uFAFF');     // CJK Compatibility Ideographs

    /// <summary>
    /// 拉丁字母與數字（含帶音標字元）。用 <see cref="char.IsLetterOrDigit(char)"/> 再排除 CJK，
    /// 這樣希臘字母、帶音標的字元等都會被保留成一個詞，而不是被當成標點丟掉。
    /// </summary>
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) && !IsCjk(c) &&
        CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.OtherLetter;
}
