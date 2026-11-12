using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.QA.Models;

/// <summary>
/// 自然語言資料查詢的一輪（規格 §11.3）。**一張表同時承擔三件事**，這是它存在的理由——
/// 為了追問狀態單獨拉一套基礎設施（Redis／Session）代價不成比例（§11.3 方案表）：
///
/// <list type="number">
/// <item><b>追問狀態</b>：<c>NeedsConfirm</c> 且 <see cref="ResolvedAt"/> 為 null 的列
///   ＝ 一輪等待使用者確認的解析結果（§9.2-3 的回顯確認需要保存上一輪解析結果）。</item>
/// <item><b>稽核語料</b>：§9.2-1 要求把「目錄沒涵蓋到的提問」留下來，作為擴充工具目錄的實證依據，
///   而不是憑空猜要建什麼工具。<c>NoTool</c> 的列就是那份實證。</item>
/// <item><b>Phase 3 的來源</b>：把驗證過的 NL 查詢另存成 <c>ReportDefinition</c> 時，
///   要存的正是這裡的 <see cref="ToolKey"/> ＋ <see cref="ParamsJson"/>。</item>
/// </list>
///
/// <para><b>🚨 這張表與「提問不落地」的架構約束不衝突，但界線必須講清楚。</b>
/// <c>QaAssistantController</c> 的類別註解說的是**提問與答案不得寫進向量庫**
/// （<c>DocChunks</c>，§8.7）——因為那張表沒有任何權限欄位擋得住，一旦寫進去，
/// 某人問題裡的單號就會出現在**別人**的檢索結果裡。本表**不是向量庫**：
/// 沒有 <c>Lexemes</c>／<c>Embedding</c>，不被任何檢索路徑讀取，
/// 唯一的讀取入口是 <see cref="UserId"/> 完全相等的那個人自己的下一輪確認。</para>
///
/// <para><b>刻意不存答案，也不存任何資料列。</b>只存提問、工具鍵、參數與筆數。
/// 摘要文字與 <c>Rows</c> 裡是實際的營運資料（金額、客戶名），而本表
/// 與 <c>DocChunk</c> 同樣沒有廠別過濾也沒有資料層級過濾——存進來就等於在一個
/// 沒有防線的地方留一份副本。筆數足夠回答 §5.5 T5 的稽核問題（誰、用哪支工具、什麼參數、幾筆）。</para>
///
/// <para>本表位於獨立的 <c>QaDbContext</c>，只在 <c>Features:QaAssistant:Enabled</c> 時建立（§17.3）。</para>
/// </summary>
public class QaTurn
{
    public long Id { get; set; }

    /// <summary>
    /// 同一次釐清對話的識別碼。第一輪（提問）與第二輪（確認後執行）共用同一個值，
    /// 使稽核看得出「使用者把模型選的東西改成了什麼」。
    /// </summary>
    public Guid ThreadId { get; set; }

    /// <summary>
    /// 本次對話的第幾輪，自 1 起。上限見 <c>QaToolQueryService.MaxRounds</c>——
    /// §11.3：釐清問答上限 1–2 輪，超過即退回「請改用畫面查詢」。
    /// </summary>
    public int RoundNo { get; set; }

    /// <summary>
    /// 提問者。**這是本表唯一的存取邊界**（§11.3：鍵必須含 user id，永不跨使用者共用）。
    /// 查詢一律以 <c>Id</c> ＋ 本欄同時比對——只用 <c>Id</c> 查，別人就能拿別人的
    /// <c>turnId</c> 接著執行，而那一次執行會用**他自己的**權限跑，
    /// 症狀是「有人查到了他本來問不出來的東西」，且沒有任何錯誤訊息。
    /// </summary>
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>使用者原本的提問。確認那一輪沿用第一輪的字面，不重問一次。</summary>
    [MaxLength(500)]
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// <c>QaToolQueryOutcome</c> 的**名稱**，不是序數。
    /// 列舉中間插一個成員會讓既有的列默默改變意義，而畫面與查詢都不會報錯
    /// （與 <c>ReportDefinition</c> 的 <c>RelativePeriod</c> 同一個坑，Phase 0-B 已踩過）。
    /// </summary>
    [MaxLength(40)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>模型選定（或使用者確認）的工具鍵。<c>NoTool</c> 時為 null。</summary>
    [MaxLength(100)]
    public string? ToolKey { get; set; }

    /// <summary>參數 JSON，形狀與 <c>ReportDefinition.ParamsJson</c> 一致（Phase 3 直接沿用）。</summary>
    public string? ParamsJson { get; set; }

    /// <summary>
    /// 這一輪可供確認的候選，JSON 陣列 <c>[{"toolKey":…,"paramsJson":…}]</c>。
    ///
    /// <para>**存的是「工具鍵＋該工具的參數」成對，不是只有鍵**：模型一次選多支時，
    /// 每一支各自帶著自己的參數，只留鍵會讓使用者改選第二支之後套上第一支的參數——
    /// 那份參數看起來完全合理（欄位名對得上），只是問的不是同一件事。</para>
    ///
    /// <para>**確認那一輪只接受這份清單裡的鍵**，否則 <c>turnId</c> 會變成一個
    /// 「用任意工具鍵執行」的入口——即使權限仍會擋，那也已經不是使用者問的那件事。</para>
    /// </summary>
    public string? CandidatesJson { get; set; }

    /// <summary>回傳筆數（§5.5 T5 的稽核要求）。未執行的輪次為 0。</summary>
    public int RowsReturned { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 這一輪的結局已定的時刻。**null ＝ 還在等使用者確認**，也就是唯一可被
    /// 下一輪接續的狀態；已填值的列一律不得再次執行（防重放）。
    /// </summary>
    public DateTime? ResolvedAt { get; set; }
}
