using System.Text.Json.Serialization;

namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 一則引文在對話畫面上的呈現方式（定案 #47／規格 §10.11.12）。
///
/// <para><b>三種而不是兩種，是因為第三種必須看得出來。</b>「模型引了一個沒給過的頁碼」
/// 與「引了一頁但那頁剛好沒內容」在畫面上會長得一模一樣，除非明講。</para>
///
/// <para><b><c>JsonStringEnumConverter</c> 不是為了好讀，是為了不靜默壞掉。</b>
/// 本專案沒有全域註冊字串列舉轉換器，預設會序列化成 <c>0/1/2</c>，
/// 而畫面比對的是 <c>c.kind === 'Rule'</c>——數字永遠不相等，於是**每一則引文都會掉進
/// 「無法查證」那一支**，把正常的手冊連結全部標成幻覺。沒有例外、沒有紅燈，
/// 只有一頁看起來像模型在亂編的答案。</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QaCitationKind
{
    /// <summary>
    /// 使用手冊頁（<c>/docs/user/&lt;module&gt;/…</c>）→ <b>維持真連結</b>，行為完全不變。
    /// 那些檔案真的在 <c>wwwroot</c> 底下。
    /// </summary>
    Manual,

    /// <summary>
    /// 規則說明擷取物（<c>/docs/user/rules/…</c>）→ <b>就地展開</b>，不畫連結。
    ///
    /// <para><c>rules/&lt;slug&gt;.html</c> **只是頁面代號，不是網址**——磁碟上沒有這個檔，
    /// 而且刻意不產（定案 #47：<c>UseStaticFiles</c> 排在 <c>UseAuthentication</c> 之前，
    /// 產出來就是匿名可讀）。內容改由 <see cref="QaCitationView.Text"/> 隨答案一起送出。</para>
    /// </summary>
    Rule,

    /// <summary>
    /// 模型引了**沒有給過它**的頁碼（幻覺引文）→ 純文字，不展開也不連結。
    ///
    /// <para>實測到的行為，不是理論風險（六顆候選中出現 0～15 次）。
    /// 展開一個空白區塊會把「模型編了一個頁碼」偽裝成「這頁剛好沒內容」。</para>
    /// </summary>
    Unverifiable,
}

/// <summary>
/// 答案底下的一則引文（<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明）。
/// </summary>
/// <param name="PageCode">模型輸出的頁面代號原文，如 <c>inv/stock-ledger.html</c>。</param>
/// <param name="HeadingPath">麵包屑；<see cref="QaCitationKind.Unverifiable"/> 時為空字串。</param>
/// <param name="Text">
/// 就地展開的內容。**只有 <see cref="QaCitationKind.Rule"/> 會有值**——
/// 手冊點得進去，不需要把整頁再送一次；幻覺引文根本沒有內容可送。
/// </param>
/// <param name="Url">
/// 可點的連結；<see cref="QaCitationKind.Manual"/> 以外一律為空字串。
/// <b>空字串代表畫面不得畫 <c>&lt;a&gt;</c></b>，不是「連到首頁」。
/// </param>
/// <param name="Kind">呈現方式。</param>
public readonly record struct QaCitationView(
    string PageCode,
    string HeadingPath,
    string Text,
    string Url,
    QaCitationKind Kind);
