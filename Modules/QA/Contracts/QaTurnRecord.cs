namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>要寫進 <c>QaTurns</c> 的一輪（§11.3）。</summary>
/// <param name="ThreadId">同一次釐清對話的識別碼；第二輪沿用第一輪的值。</param>
/// <param name="RoundNo">第幾輪，自 1 起。</param>
/// <param name="UserId">提問者。空字串代表沒有可辨識的使用者，此時整張表不寫（見 <see cref="IQaTurnStore"/>）。</param>
/// <param name="Resolved">
/// 這一輪的結局是否已定。<c>false</c> ＝ 等待確認（<c>ResolvedAt</c> 留 null），
/// 是唯一可被下一輪接續的狀態。
/// </param>
public readonly record struct QaTurnDraft(
    Guid ThreadId,
    int RoundNo,
    string UserId,
    string Question,
    string Outcome,
    string? ToolKey,
    string? ParamsJson,
    string? CandidatesJson,
    int RowsReturned,
    bool Resolved);

/// <summary>一支可供確認的候選工具與它自己的參數。</summary>
public readonly record struct QaTurnCandidate(string ToolKey, string ParamsJson);

/// <summary>一輪**已經執行過**的查詢，供 Phase 3 另存成常用查詢。</summary>
public readonly record struct QaTurnExecuted(long Id, string ToolKey, string ParamsJson);

/// <summary>一輪等待確認的解析結果。</summary>
public readonly record struct QaTurnPending(
    long Id,
    Guid ThreadId,
    int RoundNo,
    string Question,
    string? ToolKey,
    string? ParamsJson,
    IReadOnlyList<QaTurnCandidate> Candidates);

/// <summary>
/// 追問狀態與 NL 查詢稽核的持久化（§11.3）。
///
/// <para><b>落表而不是 <c>IMemoryCache</c></b>：§11.3 的方案表傾向此案，因為同一張表
/// 還要同時滿足 §9.2-1 的稽核語料與 Phase 3 的另存來源；快取只解得掉三件事裡的一件，
/// 而且重啟就沒了——「按下確認卻被告知那一輪不存在」是最難解釋的失敗形狀。</para>
///
/// <para><b>沒有使用者身分時一律不寫。</b>本表的存取邊界完全建立在 <c>UserId</c> 上
/// （§11.3：鍵必須含 user id），寫進一列 <c>UserId</c> 為空的資料等於製造一列
/// 誰都對得上、也誰都對不上的孤兒。整合測試以外不會發生，但要擋在這裡而不是靠慣例。</para>
/// </summary>
public interface IQaTurnStore
{
    /// <summary>寫入一輪，回傳它的 Id。<paramref name="draft"/> 的 UserId 為空時不寫入並回 <c>null</c>。</summary>
    Task<long?> CreateAsync(QaTurnDraft draft, CancellationToken ct = default);

    /// <summary>
    /// 取一輪**尚未定局**的解析結果。<paramref name="userId"/> 不符或該輪已定局時回 <c>null</c>——
    /// 兩者刻意回同一個結果，不讓呼叫端區分「不是你的」與「已經用過了」。
    /// </summary>
    Task<QaTurnPending?> FindPendingAsync(long turnId, string userId, CancellationToken ct = default);

    /// <summary>
    /// 取一輪**已經執行到底**的查詢（<c>Answered</c>／<c>SummarySkipped</c>）。
    ///
    /// <para>Phase 3 的另存刻意以 <c>turnId</c> 為憑據、而不是收下畫面送回來的
    /// 工具鍵與參數：後者等於讓「另存」變成一個可以憑空寫入任意查詢定義的入口，
    /// 而那張定義日後還可能被掛上排程。<b>只有真的跑出過結果的那一輪才存得起來</b>
    /// ——這同時滿足 §9.2-3「另存為排程之前必須先確認過」。</para>
    /// </summary>
    Task<QaTurnExecuted?> FindExecutedAsync(long turnId, string userId, CancellationToken ct = default);

    /// <summary>把一輪標記為已定局。已定局的列不會被再次改寫（防重放）。</summary>
    Task ResolveAsync(long turnId, string userId, string outcome, CancellationToken ct = default);

    /// <summary>刪除 <paramref name="cutoffUtc"/> 之前建立的列，回傳刪除筆數。</summary>
    Task<int> TrimAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
