using System.Text;
using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 手冊 Q&amp;A 的提示詞與答案格式契約（規格 §10.8、§10.8.1、§9.2）。
///
/// <para><b>這裡定義的不只是「怎麼問模型」，而是答案的可驗證形狀。</b>
/// 提示詞強制模型在最後一行輸出 <c>來源：頁面代號</c>，理由有兩個，缺一都會出事：</para>
/// <list type="number">
///   <item><b>使用者要能點回手冊查證。</b>8B 模型在有 context 的情況下仍會把兩頁的內容
///     揉成一段話，沒有引文連結時，使用者無從分辨哪一句有依據。</item>
///   <item><b>沒有固定格式就量不到品質。</b>「答案有沒有依據」若只能人工判讀，
///     待決 #5 的模型選型就只能靠印象。有了可解析的引文行，
///     「引用的頁面是不是參考資料裡真的有的」與「引用的是不是正解頁」兩件事都可以自動計分。</item>
/// </list>
///
/// <para><b>頁面代號用相對路徑而不是標題。</b>標題會被模型改寫（「庫存 › 庫存帳本」
/// 很容易被寫成「庫存帳本查詢」），而路徑是可以字串比對的。這一條直接決定了
/// 評測能不能自動化。</para>
///
/// <para><b>拒答句是固定字串，不是「大意如此」。</b>呈現層要據此決定不畫引文區塊，
/// 評測要據此計分。若讓模型自由發揮拒答用語，兩邊都得退回模糊比對——
/// 而模糊比對會把「手冊沒有寫，但我猜是這樣」誤判成拒答，那正是最危險的一種答案。</para>
///
/// <para><b>static 類別、無狀態。</b>不要為了「日後可換提示詞」把它做成介面加註冊——
/// 提示詞換掉就是換掉，版本差異靠 git，不靠執行期抽象（§17.5 同一個理由）。</para>
/// </summary>
public static class QaPromptBuilder
{
    /// <summary>手冊頁面在站台上的根路徑；<c>DocSearchHit.DocUrl</c> 的共同前綴。</summary>
    public const string DocRoot = "/docs/user/";

    /// <summary>
    /// 查無依據時的**唯一**合法答案。呈現層與評測都比對這個字串，不做模糊比對。
    /// </summary>
    public const string NoAnswer = "使用手冊中查無相關說明。";

    /// <summary>引文行的前綴。全形冒號；解析時另接受半形，見 <see cref="Parse"/>。</summary>
    public const string CitationPrefix = "來源：";

    /// <summary>
    /// 送進 prompt 的參考資料字數上限。
    ///
    /// <para>切塊上限是 2000 字（<c>ManualDocParser.SinglePageMaxChars</c>），
    /// topK=5 時最壞情況約一萬字。CPU 推論的 prefill 時間與 prompt token 數成正比，
    /// 而開發機一律 CPU（§17.11.3）——**參考資料放越多，使用者等越久**。
    /// 超過預算時**整塊丟掉**，不截斷任何一塊：
    /// 半塊內容會讓模型引用到一段被切掉結論的規則，那比少一塊更糟。
    /// 丟掉一塊後仍繼續試下一塊（不是遇到第一個放不下就停）——
    /// 否則一塊 2000 字的長塊會連帶擋掉它後面所有短塊，而那些短塊往往更精準。</para>
    /// </summary>
    public const int DefaultContextBudget = 6000;

    /// <summary>
    /// 系統提示詞。
    ///
    /// <para><b>第 1 條是安全邊界不是風格要求</b>（§9.2）：模型在沒有依據時會流暢地
    /// 編造操作步驟，而使用者無從分辨。第 6 條的「只能列出參考資料裡真的有的頁面代號」
    /// 同理——沒有這一句，模型會憑印象補一個看起來很合理但不存在的頁面。</para>
    ///
    /// <para><b>第 2 條是 2026-08-06 實測補上的，不是想出來的。</b>
    /// 原版沒有這一條時，<c>gemma4</c> 對「想知道資金積壓在哪些料」「要抓出超過門檻天數
    /// 都沒有進出的料」這種**只說目的、沒寫成問句**的提問一律拒答——
    /// 而兩題的正解頁都好端端地排在參考資料第 1、第 2 名。
    /// 模型的理解沒有錯（手冊裡確實沒有「哪些料」的答案），錯的是提示詞沒講清楚
    /// 這個助理答的是「去哪裡看、怎麼操作」而不是「你的資料是多少」。
    /// <b>少了這一條，換模型比到的是各家如何各自解讀我留下的歧義</b>，不是模型的品質。</para>
    /// </summary>
    public static string SystemPrompt =>
        $"""
        你是 SmartFactory MES 使用手冊的問答助理。

        規則：
        1. 只能根據「參考資料」回答。參考資料沒有寫到的，一律當作不知道——
           不要用你自己的知識補充，也不要從常理推測。
        2. 使用者常常只說出目的而沒有寫成問句（「想知道哪幾項庫存積壓最多」
           「要抓出很久沒有進出的料」）。這種情況要回答**用哪個畫面或報表、怎麼操作**，
           不可以因為手冊裡沒有實際數字就說查無——手冊沒有的是資料，不是說明。
        3. 只有在參考資料連「該用哪個畫面、該怎麼做」都沒有寫時，才回覆下面這一句，
           不要有其他任何文字：
           {NoAnswer}
        4. 一律用繁體中文、台灣慣用語回答。
        5. 先直接回答，需要時再補步驟。不要重複問題，不要客套，不要說「根據參考資料」。
        6. 回答的最後一行必須是來源行，格式固定：
           {CitationPrefix}頁面代號
           頁面代號請原樣抄寫參考資料每段開頭中括號裡的字（例如 inv/stock-ledger.html），
           引用多頁時以「、」分隔。只能列出參考資料裡真的出現過的頁面代號。
        7. 第 3 點的拒答情形不要附來源行。
        """;

    /// <summary>
    /// 把檢索結果組成使用者訊息。
    ///
    /// <para><b>每段開頭的 <c>[頁面代號]</c> 是引文契約的另一半</b>——
    /// 系統提示詞叫模型「原樣抄寫中括號裡的字」，那些字就是這裡放進去的。
    /// 兩邊要一起改。</para>
    /// </summary>
    /// <param name="question">使用者的提問，原樣帶入。</param>
    /// <param name="hits">檢索結果，須已依名次排序。空集合代表零命中。</param>
    /// <param name="contextBudget">參考資料字數上限，見 <see cref="DefaultContextBudget"/>。</param>
    public static string BuildUserPrompt(
        string question, IReadOnlyList<DocSearchHit> hits, int contextBudget = DefaultContextBudget)
    {
        ArgumentNullException.ThrowIfNull(hits);

        // 零命中不組 context。§10.8：不把空的參考資料丟給模型自由發揮——
        // 那等於把「手冊沒寫」變成「模型隨便寫」。
        if (hits.Count == 0)
            return $"參考資料：（無）\n\n問題：{question}";

        var sb = new StringBuilder("參考資料：\n");
        foreach (var h in SelectWithinBudget(hits, contextBudget))
            sb.Append('\n').Append('[').Append(PageCode(h.DocUrl)).Append("] ")
              .Append(h.HeadingPath).Append('\n').Append(h.Text.Trim()).Append('\n');

        return sb.Append("\n問題：").Append(question).ToString();
    }

    /// <summary>
    /// 依字數預算挑出**真正會進 prompt** 的那幾塊。
    ///
    /// <para><b>這個方法是公開的，因為「模型看到了哪幾頁」是呼叫端必須知道的事</b>——
    /// 引文驗證要拿它比對（引了沒給過的頁＝幻覺），評測要拿它當分母。
    /// 用 <c>hits</c> 代替它的話，被預算丟掉的那一塊會被算成「有給模型看」，
    /// 於是模型引不到就變成模型的失分，而它根本沒看到那一頁。</para>
    /// </summary>
    public static IReadOnlyList<DocSearchHit> SelectWithinBudget(
        IReadOnlyList<DocSearchHit> hits, int contextBudget = DefaultContextBudget)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var kept = new List<DocSearchHit>();
        var used = 0;
        foreach (var h in hits)
        {
            var len = h.Text.Trim().Length;
            if (used > 0 && used + len > contextBudget) continue;
            kept.Add(h);
            used += len;
        }
        return kept;
    }

    /// <summary>
    /// <c>/docs/user/inv/stock-ledger.html</c> → <c>inv/stock-ledger.html</c>。
    /// 前綴不符時原樣回傳（不丟例外：提示詞裡多一段路徑不會壞，丟例外會讓整次問答失敗）。
    /// </summary>
    public static string PageCode(string docUrl) =>
        docUrl.StartsWith(DocRoot, StringComparison.Ordinal)
            ? docUrl[DocRoot.Length..]
            : docUrl;

    /// <summary>
    /// 拆出答案本文與引用的頁面代號。
    ///
    /// <para><b>只認最後一個來源行。</b>模型偶爾會在段落中間先寫一次來源、結尾再寫一次；
    /// 取最後一個與提示詞第 5 條一致。找不到來源行時 <see cref="QaAnswerParts.Citations"/>
    /// 為空——**那本身就是要量的一種失敗**，不要在這裡幫模型補一個。</para>
    /// </summary>
    public static QaAnswerParts Parse(string answerText)
    {
        var text = (answerText ?? string.Empty).Trim();
        if (text.Length == 0) return new QaAnswerParts(string.Empty, Array.Empty<string>(), false);

        // 拒答判定只看有沒有那一句。模型有時會在前面加「很抱歉，」——
        // 這仍是正確的拒答行為，不該因為多了兩個字就記成幻覺。
        var refused = text.Contains(NoAnswer, StringComparison.Ordinal);

        var lines = text.Split('\n');
        var idx = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith(CitationPrefix, StringComparison.Ordinal) ||
                t.StartsWith("來源:", StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0) return new QaAnswerParts(text, Array.Empty<string>(), refused);

        var payload = lines[idx].TrimStart()[3..];   // 去掉「來源」＋一個冒號
        var codes = payload
            .Split(new[] { '、', '，', ',', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim('[', ']', '`', '(', ')', '「', '」', '。', '*'))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var body = string.Join('\n', lines.Where((_, i) => i != idx)).Trim();
        return new QaAnswerParts(body, codes, refused);
    }
}

/// <summary>
/// 拆解後的答案（<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明）。
/// </summary>
/// <param name="Body">去掉來源行的答案本文。</param>
/// <param name="Citations">引用的頁面代號，已去重並保持模型輸出的順序。空陣列＝沒有來源行。</param>
/// <param name="IsNoAnswer">
/// 是否為拒答。<b>與 <paramref name="Citations"/> 為空是兩件事</b>——
/// 「說了不知道」和「答了但沒附來源」在評測與呈現上都要分開處理。
/// </param>
public readonly record struct QaAnswerParts(
    string Body,
    IReadOnlyList<string> Citations,
    bool IsNoAnswer);
