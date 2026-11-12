using System.Text.Json.Serialization;

namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 一份來源檔的擷取物清單（規格 §10.11.9）。**一個來源檔一份 manifest**，
/// 存於 <c>.claude/qa-extracts/</c> 並進版控。
///
/// <para><b>核可＝這份檔案被 merge 進 main</b>（定案 #43 的落地形式）。
/// 之所以把人工核可放進 git 而不是做成畫面，是三件已查證的事逼出來的：</para>
/// <list type="number">
///   <item><c>QaDbContext</c> 沒有任何攔截器，畫面上的核可**不會留下稽核紀錄**——
///     而 §10.11.4 說 <c>Approved</c> 是唯一防線。</item>
///   <item>§10.11.6 已定案索引在建置期產生並隨 DB 出貨；畫面改的是正式機當下的 DB，
///     兩者會漂開，且事後無法回答「當時到底核可了什麼」。</item>
///   <item>核可粒度是段落（定案 #37），語料是 17,331 行規格加程式碼——
///     這是 code review 等級的工作量，而 diff、上次審到哪、退回重來，git 天生就有。</item>
/// </list>
///
/// <para><b>領域專家不需要會 git</b>：審核的判斷交給懂規則的人（產生器會另外輸出人可讀的審核頁），
/// 提交留在版控。這兩件事刻意分開。</para>
/// </summary>
public sealed class ExtractManifest
{
    /// <summary>擷取來源的 repo 相對路徑，如 <c>.claude/specifications/currency.md</c>。</summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 擷取當下來源檔全文的 SHA256（小寫十六進位，64 字）。
    ///
    /// <para><b>匯入時會拿現檔重算並比對</b>，不符即整份標記 stale（§10.11.3）。
    /// 這個欄位是人寫的（由產生器填），所以它記錄的是「這批文字是根據哪一版原文寫的」，
    /// 而不是「現在的原文長什麼樣」——兩者一致才代表這批擷取物還有效。</para>
    /// </summary>
    [JsonPropertyName("sourceHash")]
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>逐段的擷取物。順序即 <c>DocChunk.ChunkIndex</c>，故重排順序會造成整份重寫。</summary>
    [JsonPropertyName("entries")]
    public List<ExtractEntry> Entries { get; set; } = new();
}

/// <summary>manifest 裡的一段擷取物＝索引裡的一列 <c>DocChunk</c>。</summary>
public sealed class ExtractEntry
{
    /// <summary>
    /// 這一段的穩定識別字，小寫英數與連字號，如 <c>currency-rate-direction</c>。
    ///
    /// <para>它決定引文頁碼（<c>rules/&lt;slug&gt;.html</c>）。<b>形狀刻意與手冊頁碼一致</b>——
    /// 2026-08-13 的三輪實測顯示模型是在對齊一個具體字面而非讀規則
    /// （見 §10.8.2 之十一），換一種形狀的頁碼等於在沒有量測的情況下動引文行為。</para>
    /// </summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary><c>zh</c> 或 <c>en</c>。檢索時是硬過濾條件（§10.4）。</summary>
    [JsonPropertyName("lang")]
    public string Lang { get; set; } = string.Empty;

    /// <summary>麵包屑，同時是引文標題，也併入切詞。</summary>
    [JsonPropertyName("headingPath")]
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>
    /// 面向使用者的規則說明本文。**不是原文**（§10.11.1）——
    /// 原文有大量已被推翻的舊方案、開發者語彙，而模型分不出一句話前面掛著「不採用」。
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 人工核可（§10.11.4）。**產生器一律寫 <c>false</c>**，改成 <c>true</c> 是人的動作。
    ///
    /// <para>未核可的段落照樣匯入索引（會嵌入、佔一列），但被
    /// <c>DocChunkFilter</c> 擋在檢索之外。保留而不丟棄是刻意的：
    /// 審核者要看得到「這一段被產出來但我沒放行」，丟掉的話下一次產生器會再產一次同樣的東西。</para>
    /// </summary>
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    /// <summary>
    /// 這一段**來源原文**的 SHA256，由 <c>scripts/qa-extract-reconcile.py</c> 寫入。
    ///
    /// <para><b>C# 這一側刻意不拿它算 stale。</b>要算就得在 C# 再實作一次 markdown
    /// 的切段規則（追圍籬、維護標題路徑），而兩份實作漂開的症狀正是
    /// <c>DocChunk.IsStale</c> 的註解寫的那兩種：全部誤判為 stale（整個語料消失），
    /// 或一律當作新鮮（閘門形同虛設）。檔案層級的 <see cref="ExtractManifest.SourceHash"/>
    /// 是保守的那一邊——寧可多凍結，不會漏放行。</para>
    ///
    /// <para>它存在是為了讓對帳腳本**下一次不必去 git 挖原文**：目前還原「擷取當時的
    /// 原文」靠的是逐一比對該檔的歷史版本，而那條路只在原文曾被 commit 時走得通。</para>
    /// </summary>
    [JsonPropertyName("sourceSectionHash")]
    public string SourceSectionHash { get; set; } = string.Empty;
}
