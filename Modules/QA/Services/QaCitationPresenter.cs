using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 把模型輸出的引文行變成畫面上的三種呈現（定案 #47／規格 §10.11.12）。
///
/// <para><b>純函式、無相依，刻意獨立於 Controller 之外。</b>它是 S-7 唯一
/// 「不必有推論服務就能完整驗證」的部分——而它同時是這個畫面**最容易悄悄壞掉**的部分：
/// 判錯一則引文的類別不會拋例外、不會變紅，只會讓使用者拿到一條點不下去的連結，
/// 或更糟，讓一個編出來的頁碼看起來像真的。</para>
///
/// <para><b>比對一律用序數、區分大小寫</b>（<c>List&lt;string&gt;.Contains</c> 的預設語意）。
/// 這不是隨手選的：<c>ManualGenerationBenchmarkTests</c> 判定幻覺引文用的就是
/// <c>given.Contains(c)</c>。這裡若放寬成不分大小寫，畫面會把評測記為幻覺的那一則
/// 當成可查證的引文放行——**量到的品質與使用者看到的東西從此不是同一件事**。</para>
/// </summary>
public static class QaCitationPresenter
{
    /// <summary>擷取物的頁面代號前綴。<c>DocUrl</c> 是 <c>/docs/user/rules/…</c>，去掉 DocRoot 後即此。</summary>
    public const string RulePrefix = "rules/";

    /// <summary>
    /// 依「這一輪真的進了 prompt 的那幾塊」把引文分類。
    /// </summary>
    /// <param name="citations">
    /// <c>QaPromptBuilder.Parse</c> 拆出的頁面代號，順序即模型輸出的順序（已去重）。
    /// </param>
    /// <param name="given">
    /// <b>必須是 <c>QaPromptBuilder.SelectWithinBudget</c> 的結果，不是檢索回傳的全部。</b>
    /// 被字數預算丟掉的塊模型根本沒看到，拿 <c>hits</c> 當依據會把「沒看到的頁」
    /// 當成可查證來源，而它的內容從來沒有進過這則答案。
    /// </param>
    /// <param name="lang">
    /// 答覆語系（<c>"zh"</c>／<c>"en"</c>）。只影響手冊連結要不要帶 <c>?lang=en</c>——
    /// 擷取物就地展開，內容早已依語系檢索過，不需要也不能帶語系參數。
    /// </param>
    public static IReadOnlyList<QaCitationView> Build(
        IReadOnlyList<string> citations, IReadOnlyList<DocSearchHit> given, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(given);

        // 同一頁可能被檢索切成多塊（手冊長頁）。取第一塊即可：
        // 手冊靠連結點過去看全文，擷取物則是「一 slug 一塊」
        // （ExtractManifestImporter.BuildRows 是 entries.Select((e, i) => …)），
        // 所以這裡的「第一塊」對擷取物而言就是唯一那一塊。
        var byCode = new Dictionary<string, DocSearchHit>(StringComparer.Ordinal);
        foreach (var h in given)
        {
            var code = QaPromptBuilder.PageCode(h.DocUrl);
            if (!byCode.ContainsKey(code)) byCode[code] = h;
        }

        var result = new List<QaCitationView>(citations.Count);
        foreach (var code in citations)
        {
            if (!byCode.TryGetValue(code, out var hit))
            {
                // 沒給過的代號。**保留並標示**，不是丟掉——丟掉的話，
                // 「模型編了頁碼」與「模型只引了一頁」在畫面上完全一樣，
                // 而前者是使用者最該提高警覺的訊號。
                result.Add(new QaCitationView(code, string.Empty, string.Empty,
                    string.Empty, QaCitationKind.Unverifiable));
                continue;
            }

            if (code.StartsWith(RulePrefix, StringComparison.Ordinal))
            {
                result.Add(new QaCitationView(
                    code, hit.HeadingPath, hit.Text.Trim(), string.Empty, QaCitationKind.Rule));
                continue;
            }

            result.Add(new QaCitationView(
                code, hit.HeadingPath, string.Empty,
                HelpDocResolver.WithLang(hit.DocUrl, lang), QaCitationKind.Manual));
        }

        return result;
    }
}
