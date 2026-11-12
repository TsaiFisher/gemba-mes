using System.Security.Claims;

namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>一次自然語言查詢的結局。</summary>
public enum QaToolQueryOutcome
{
    /// <summary>選到工具、查到資料、也產出了白話摘要。</summary>
    Answered,

    /// <summary>
    /// 查到資料了，但**摘要那一段沒完成**（逾時或失敗）。
    /// 這不是失敗：資料已經在手上，丟掉它等於讓使用者白等（§17.8「降級要出聲」）。
    /// </summary>
    SummarySkipped,

    /// <summary>模型判定沒有對應的工具（§9.2 的誠實拒答）。這是正常路徑。</summary>
    NoTool,

    /// <summary>模型回了一個**不在授權清單內**的工具名。已擋下（§8.7）。</summary>
    ToolRejected,

    /// <summary>
    /// 已解析出工具與參數，但依 §9.2-3／§9.3 **必須先回顯確認才能執行**（§11.3）。
    /// <b>這一輪什麼都還沒查</b>——資料庫沒被碰過，摘要也沒跑。
    /// </summary>
    NeedsConfirm,

    /// <summary>
    /// 要確認的那一輪找不到：已被保留期修剪、已經執行過（重放）、或根本不是這位使用者的。
    /// 三種原因刻意回同一個結局，見 <c>IQaTurnStore.FindPendingAsync</c>。
    /// </summary>
    ConfirmExpired,

    /// <summary>
    /// 超過釐清輪數上限（§11.3：上限 1–2 輪），退回「請改用畫面查詢」。
    /// </summary>
    TooManyRounds,

    /// <summary>
    /// 目前設定的推論端點不被信任承載營運資料，整題**在碰資料庫之前**就擋下
    /// （§17.11.8，見 <c>IInferenceEgressPolicy</c>）。
    ///
    /// <para><b>這不是權限問題也不是模型問題</b>：同一位使用者、同一句提問，
    /// 把端點改回本機就會正常執行。所以訊息必須指向組態而不是指向使用者，
    /// 否則現場只會得到「他有權限卻查不到」這個查不出原因的結論。</para>
    /// </summary>
    EgressBlocked,
}

/// <summary>為什麼這一輪需要回顯確認（§11.3）。**判定一律是確定性的，不問模型**。</summary>
public enum QaConfirmReason
{
    /// <summary>不需要確認。</summary>
    None,

    /// <summary>
    /// 工具的輸出含金額（<c>IReportToolDescriptor.ContainsMonetaryValues</c>）。
    /// §9.3：成本口徑歧義比日期歧義更危險，**一律強制回顯**，不走「有歧義才問」的判斷——
    /// 系統同時有 AVCO／標準成本／實際成本／Preliminary 回溯多套口徑並行，
    /// 選錯時答案看起來完全合理、數字也自洽。
    /// </summary>
    Monetary,

    /// <summary>
    /// 模型一次選了多支工具。**先前的行為是取第一支並只留一行 log**——
    /// 使用者看不到自己問的兩件事只被答了一件。改為讓使用者挑（§11.3）。
    /// </summary>
    MultipleTools,
}

/// <summary>
/// 一支候選工具在回顯畫面上的完整形狀：schema 決定畫成什麼控制項，
/// <paramref name="ParamsJson"/> 決定每個控制項的初始值。
///
/// <para><b>兩者必須成對送到畫面。</b>只送 schema 的話，使用者改選第二支之後
/// 畫面沒有值可填，回顯就退化成「請你自己重填一次」；只送值的話，
/// 列舉畫不成下拉，使用者只能猜合法字面。</para>
/// </summary>
public sealed record QaConfirmCandidate(QaToolDefinition Definition, string ParamsJson);

/// <summary>
/// 自然語言查詢的結果。
///
/// <para>用 <c>readonly record struct</c> 跟隨 <c>ReportToolRunResult</c> 的既有慣例，
/// 同時避開 R5b 對「Modules 命名空間下 public class 的非可空實值型別屬性」的誤報。</para>
/// </summary>
/// <param name="TurnId">
/// 本輪在 <c>QaTurns</c> 的 Id。<c>NeedsConfirm</c> 時**必有值**，是下一輪確認的憑據；
/// 其餘結局可能為 null（沒有可辨識的使用者時整張表不寫，見 <c>IQaTurnStore</c>）。
/// </param>
/// <param name="ConfirmReason">需要確認的理由；<c>NeedsConfirm</c> 以外一律為 <c>None</c>。</param>
/// <param name="Candidates">
/// 可供確認的候選工具（schema ＋ 模型替它填的參數）。<c>NeedsConfirm</c> 時填，
/// 供畫面回顯與編修；**也界定了確認那一輪允許送回哪些工具鍵與哪些參數**（§8.7）。
/// </param>
public readonly record struct QaToolQueryResult(
    QaToolQueryOutcome Outcome,
    string Answer,
    string? ToolKey,
    string? ParamsJson,
    string[]? Headers,
    IReadOnlyList<object?[]>? Rows,
    string? Note,
    long? TurnId = null,
    QaConfirmReason ConfirmReason = QaConfirmReason.None,
    IReadOnlyList<QaConfirmCandidate>? Candidates = null);

/// <summary>
/// 把提問接上工具目錄與執行（§9.1 的五段流程）。
///
/// <para><b>LLM 全程不碰資料庫。</b>它只做兩件事：前段輸出「工具鍵＋參數」，
/// 後段把已經查出來的資料寫成白話。中間的執行是應用程式碼跑 Phase 0-B 已驗證的
/// <c>IReportTool</c>，權限、廠別、資料層級過濾全部照既有機制走——
/// 這整個流程在性質上等同一次內部 API 呼叫。</para>
/// </summary>
public interface IQaToolQueryService
{
    Task<QaToolQueryResult> AskAsync(string question, ClaimsPrincipal? user, CancellationToken ct = default);

    /// <summary>
    /// 確認並執行前一輪解析出來的查詢（§11.3 第二輪）。
    ///
    /// <para><b>🚨 這一輪必須重跑完整的權限檢查，不得沿用第一輪的授權結論</b>（§8.7／§11.3）：
    /// 兩輪之間可能隔了幾分鐘，中間角色可能被改、工具可能被停用。</para>
    ///
    /// <para><paramref name="edits"/> 是使用者在回顯畫面上改過的參數。
    /// <b>只有第一輪 schema 公布過的欄位會被採用</b>，其餘一律沿用存下來的那一份——
    /// 允許任意欄位等於讓確認畫面變成一個比模型還寬的參數入口，
    /// 而模型連 <c>plantId</c>／<c>dateBasis</c> 都刻意拿不到（<c>QaToolSchemaBuilder.Excluded</c>）。</para>
    /// </summary>
    Task<QaToolQueryResult> ConfirmAsync(
        long turnId, string? toolKey, IReadOnlyDictionary<string, string?>? edits,
        ClaimsPrincipal? user, CancellationToken ct = default);
}
