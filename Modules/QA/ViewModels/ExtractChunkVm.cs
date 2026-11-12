namespace SmartFactory.MES.Modules.QA.ViewModels;

/// <summary>
/// 擷取物只讀畫面的一列（規格 §10.11.11「只有這一支適合做成畫面」）。
///
/// <para><b>刻意是 class 而不是 record struct</b>：它會被 EF 的 <c>Select</c> 投影出來，
/// 而值型別屬性在 R5b 的掃描下要另外處理——這裡沒有 record struct 帶來的任何好處。</para>
/// </summary>
public class ExtractChunkVm
{
    /// <summary>引文頁碼（<c>rules/&lt;slug&gt;.html</c>）。這正是模型在答案裡會印出來的字串。</summary>
    public string PageCode { get; set; } = string.Empty;

    /// <summary>來源規格檔的 repo 相對路徑，如 <c>.claude/specifications/currency.md</c>。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>麵包屑，同時是引文標題。</summary>
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>規則說明本文。**未核可時不帶出來**，理由見 <c>QaExtractController</c>。</summary>
    public string Text { get; set; } = string.Empty;

    public string Lang { get; set; } = string.Empty;

    /// <summary>人工核可（manifest 的 <c>approved</c> 被 merge 進 main）。</summary>
    public bool Approved { get; set; }

    /// <summary>來源檔已變動而 manifest 沒重生。過期＝直接排除於檢索之外（§10.11.3）。</summary>
    public bool IsStale { get; set; }

    /// <summary>
    /// **匯入時間，不是核可時間。**核可＝manifest 被 merge（定案 #45），
    /// 那個時間點只存在於 git，DB 裡沒有任何欄位承載它。
    /// 欄名照實叫匯入時間，是為了不讓人把它當成「誰在什麼時候放行了這一段」。
    /// </summary>
    public DateTime IndexedAt { get; set; }
}
