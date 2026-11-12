using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules._Shared;

/// <summary>
/// 「這張待簽核的單，簽核人在手機上該看到什麼」——一個 DocumentType 一份。
///
/// <para><b>為什麼需要這個抽象</b>：<c>ApprovalRequest</c> 上只有單號、型別、申請人、
/// 送審時間、步驟序號，**沒有金額、沒有品項、沒有摘要**。桌機的簽核判斷資訊完全來自
/// 點單號跳去各模組的 Details 頁，而手機一個 Details 頁都沒有。
/// 在這種狀態下給一顆「核准」，等於做出一個**看不到內容卻按得下去**的按鈕——
/// 那是把簽核變成橡皮圖章，而且是在最容易誤觸的裝置上。
/// 所以手機能不能簽，先決條件是這份摘要，不是那支 API。</para>
///
/// <para><b>註冊方式與 <see cref="Services.IApprovalSyncContributor"/> 相同</b>：
/// 各模組在自己的 <c>IAppModule.RegisterServices</c> 裡
/// <c>services.AddScoped&lt;IApprovalSummaryProvider, XxxApprovalSummary&gt;()</c>。
/// 刻意沿用同一個形狀——那個擴充點已經有 30 個實作，多發明一種註冊法只會讓人記錯。</para>
///
/// <para>⚠️ <b>沒有 provider 的單別不是錯誤，是「手機簽不了」。</b>
/// 手機端必須明確顯示「此單別請用電腦簽核」並給出桌機連結，
/// <b>不可以</b>顯示一張空白摘要配一顆核准鈕。哪些單別已實作、哪些刻意還沒做，
/// 由 <c>ApprovalSummaryCoverageTests</c> 與 <c>ApprovalSummaryPending.txt</c> 一起釘住：
/// 新增送簽單別時測試會紅，逼你二選一（實作，或明寫還沒做並附理由）。</para>
/// </summary>
public interface IApprovalSummaryProvider
{
    /// <summary>處理哪一種 DocumentType（與 <c>SubmitForApproval</c> 傳入的一致）。</summary>
    string DocumentType { get; }

    /// <summary>
    /// 取這張單的摘要。查不到單據（已刪除、跨廠被全域過濾擋掉）回 <c>null</c>——
    /// 呼叫端要把 null 顯示成「這張單已不存在或你沒有權限看」，不是顯示成空白。
    /// </summary>
    Task<ApprovalSummary?> GetAsync(ApplicationDbContext db, string documentId, CancellationToken ct = default);
}

/// <summary>
/// 簽核摘要。刻意做成**與模組無關的扁平結構**：手機端只有一套版面，
/// 每個模組各自回傳自己的形狀會讓前端長出 30 個分支。
/// </summary>
/// <param name="DocumentId">單號（畫面標題）。</param>
/// <param name="PlantId">廠別。跨廠簽核時這是判斷「這是不是我該簽的」的關鍵之一。</param>
/// <param name="Counterparty">往來對象（供應商／客戶／領用單位）。沒有就 null。</param>
/// <param name="AmountLocal">
/// 本幣金額。<b>null 代表這張單沒有金額概念</b>（例如工單、調撥），
/// 不是「金額是 0」——手機端要把兩者顯示成不同的東西，
/// 否則「金額 0 元的採購單」與「工單」在畫面上長得一樣。
/// </param>
/// <param name="CurrencyCode">交易幣別；<paramref name="AmountLocal"/> 為 null 時無意義。</param>
/// <param name="Rows">
/// 主檔欄位（標籤 → 值）。標籤請用 <c>IStringLocalizer</c> 取得的**已在地化字串**，
/// 不是資源鍵——手機端沒有那些鍵的字典，直接顯示鍵會變成一排英文代碼。
/// </param>
/// <param name="Lines">明細（最多帶 <see cref="MaxLines"/> 筆）。</param>
/// <param name="TotalLineCount">明細總筆數；大於 <see cref="MaxLines"/> 時畫面要顯示「還有 N 筆」。</param>
public sealed record ApprovalSummary(
    string DocumentId,
    string? PlantId,
    string? Counterparty,
    decimal? AmountLocal,
    string? CurrencyCode,
    IReadOnlyList<ApprovalSummaryRow> Rows,
    IReadOnlyList<ApprovalSummaryLine> Lines,
    int TotalLineCount)
{
    /// <summary>
    /// 明細最多帶幾筆。手機畫面本來就放不下更多，而把整張 200 行的單送到手機上
    /// 只是讓網路更慢、讓人更難找到重點。超過的筆數由 <see cref="TotalLineCount"/> 表達。
    /// </summary>
    public const int MaxLines = 20;
}

/// <summary>主檔的一個欄位。</summary>
public sealed record ApprovalSummaryRow(string Label, string Value);

/// <summary>
/// 明細的一行。刻意只留這四欄——簽核人要判斷的是「買什麼、多少、多少錢」，
/// 而不是重現整張單。要看全部就開桌機連結。
/// </summary>
public sealed record ApprovalSummaryLine(
    string Description,
    decimal? Qty,
    string? UomCode,
    decimal? Amount);
