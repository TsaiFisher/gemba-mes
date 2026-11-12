using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Data;

/// <summary>
/// 詞彙通道與語意通道**共用的**過濾條件（規格 §10.4 語系、§10.10 權限）。
///
/// <para><b>存在的唯一理由是防止兩個通道的權限判定各寫一份。</b>
/// 兩份實作遲早會不一致，而不一致的後果是：某個通道讓使用者看到他無權讀的畫面說明，
/// 或某個通道莫名其妙少回幾筆。兩者都不會有錯誤訊息。</para>
///
/// <para>刻意是 SQL 片段而不是 <c>Expression&lt;Func&lt;DocChunk, bool&gt;&gt;</c>：
/// 詞彙通道必須走 <c>FromSqlRaw</c>（<c>ts_rank</c> 沒有 LINQ 對應），
/// 為了共用而把它改寫成 LINQ 是不可能的，所以共用點只能落在 SQL 這一層。</para>
/// </summary>
internal static class DocChunkFilter
{
    /// <summary>
    /// 參數位置固定：<c>{0}</c>＝語系、<c>{1}</c>＝可讀的 MenuKey、<c>{2}</c>＝可讀的模組。
    /// 使用端的其餘參數一律從 <c>{3}</c> 起編號。
    ///
    /// <para><b>最外層是語料別（<c>Corpus</c>）的二選一，兩套權限規則各管一邊</b>（§10.11.7 定案 #42）。
    /// 兩個分支刻意並排寫在同一段字串裡，不拆成兩個常數也不拆成兩個方法：
    /// 同一張表兩套規則的實際失敗模式是「改了一套、忘了另一套」，
    /// 而那要嘛讓使用者看到無權讀的內容、要嘛讓一整批語料安靜地檢索不到——兩者都沒有錯誤訊息。
    /// 逼它們在視線內並排，是唯一有效的防呆。</para>
    ///
    /// <para><b>手冊分支（<c>manual</c>）的四個 OR：</b></para>
    /// <list type="number">
    ///   <item>有對應畫面的頁面 → 必須有該畫面的讀取權。</item>
    ///   <item>手冊首頁（<c>MenuKey</c> 與 <c>Module</c> 皆空）→ 只有通用 FAQ，維持公開。</item>
    ///   <item>模組目錄頁（<c>MenuKey</c> 空、<c>Module</c> 有值）→ 它列出整個模組有哪些畫面，
    ///     對該模組毫無權限的人不該檢索得到，那是系統功能結構的洩漏。</item>
    ///   <item>無 controller 的公開目錄（<see cref="ManualCorpusPolicy.PublicModules"/>）→
    ///     見該欄位的說明。少了這一條，<c>mobile/</c> 底下的內容永遠檢索不到。</item>
    /// </list>
    ///
    /// <para><b>擷取物分支（<c>extract</c>）不做權限過濾，改成兩個內容閘門</b>（§10.11.4／§10.11.5）：
    /// <c>Approved</c> 為人工核可、<c>IsStale</c> 為來源已變動。
    /// 擷取物不綁畫面故無 MenuKey 可比，<b>安全邊界因此從執行期權限過濾搬到索引前的內容審核</b>——
    /// 這兩道關的失敗方向差很多：權限過濾漏了會有人抱怨看不到東西，內容審核漏了不會有任何訊號。
    /// 這也是為什麼這裡要用 <c>AND</c> 串兩個閘門而不是任一即可：兩者防的是不同的錯
    /// （寫錯 vs 過期），少任何一個都不夠。</para>
    ///
    /// <para><b>語系條件 2026-08-27 從最外層搬進兩個分支</b>（§17.9 缺口 G4）：
    /// 原本一句 <c>c."Lang" = {0}</c> 同時管兩邊，讀起來像一件事，實際上做的是兩件——
    /// 見 <see cref="ExtractLangSql"/>。搬動本身**不改變任何行為**
    /// （<c>A AND (B OR C)</c> ≡ <c>(A AND B) OR (A AND C)</c>），
    /// 改變行為的只有 <paramref name="crossLangExtract"/>，預設值在
    /// <c>Features:QaAssistant:ExtractCrossLang</c>，**2026-08-27 裁決為 <c>true</c>**。</para>
    ///
    /// <para><b>為什麼不是 <c>const</c>：</b>手冊第 4 分支的白名單改成集合後就不再是編譯期常數了。
    /// 它**不能**改用 <c>FromSqlRaw</c> 參數傳入：參數編號 <c>{0}</c>～<c>{2}</c> 是與所有呼叫端的約定
    /// （其餘參數一律從 <c>{3}</c> 起編），多塞一個會把每個呼叫端的編號都推移一位。
    /// <b>語料別分支同理只用字面值、不新增參數</b>，故本次擴充沒有動到任何呼叫端的編號。
    /// 拼進 SQL 的字面全部是程式碼裡寫死的識別字、不含使用者輸入，沒有注入面；
    /// 「只能是小寫英數字」由 <c>ManualReachabilityTests</c> 與 <c>DocCorpusFacts</c> 斷言把關。</para>
    /// </summary>
    public static string LangAndPermission(bool crossLangExtract) => $$"""
        (
              (
                c."Corpus" = '{{DocCorpus.Manual}}'
                AND c."Lang" = {0}
                AND (
                      (c."MenuKey" <> '' AND c."MenuKey" = ANY({1}))
                   OR (c."MenuKey" =  '' AND c."Module"  = '')
                   OR (c."MenuKey" =  '' AND c."Module"  = ANY({2}))
                   OR (c."MenuKey" =  '' AND c."Module"  IN ({{PublicModulesSql}}))
                )
              )
           OR (
                c."Corpus" = '{{DocCorpus.Extract}}'
                AND {{ExtractLangSql(crossLangExtract)}}
                AND c."Approved" AND NOT c."IsStale"
              )
        )
        """;

    /// <summary>
    /// 擷取物分支的語系條件。<c>TRUE</c> 代表**不比語系**（§17.9 缺口 G4 的候選修法 B）。
    ///
    /// <para><b>手冊分支永遠比語系，這裡刻意只放寬擷取物那一邊。</b>兩者的失敗方向不同：
    /// 手冊有中英兩份，不比語系就會把英文段落混進中文答案裡（§10.9 S2-e 那次的假缺陷
    /// 正是量測漏掉這個條件造成的）；擷取物<b>只有中文一份</b>，比語系的結果不是混語言，
    /// 而是英文提問一段都拿不到——**同一個條件在兩個語料別上做的是兩件事**，
    /// 所以它不能再放在最外層一次寫完。</para>
    /// </summary>
    private static string ExtractLangSql(bool crossLangExtract) =>
        crossLangExtract ? "TRUE" : "c.\"Lang\" = {0}";

    private static string PublicModulesSql =>
        string.Join(", ", ManualCorpusPolicy.PublicModules.Select(m => $"'{m}'"));
}
