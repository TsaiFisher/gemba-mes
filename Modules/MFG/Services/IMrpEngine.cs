namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// MRP 引擎 — 把一張 MrpRun（含已選入的 MrpRunLine）展開為生產批、工單、
    /// 工單工序/用料、派工/派工用料，並依 CD_MRP 觸發採購/委外/自製遞迴
    /// （mfg-domain.md §5.1）。
    /// </summary>
    public interface IMrpEngine
    {
        /// <summary>
        /// 展開指定的 MRP 執行單（須為 Draft 狀態）。完成後 MrpRun 轉 Executed。
        /// </summary>
        /// <exception cref="MrpBomDepthExceededException">BOM 巢狀超過 20 層。</exception>
        Task<MrpRunResult> RunAsync(string mrpRunId, CancellationToken ct = default);

        /// <summary>
        /// 重跑已展開的 MRP 執行單（mfg-domain.md §10-5）：清除前次展開後重新展開。
        /// 已投產（InProgress/Completed/Closed）工單存在時不可重跑。
        /// 執行前一律先跑 <see cref="EvaluateReRunAsync"/>，等級為
        /// <see cref="MrpReRunLevel.Blocked"/> 即拋例外中止。
        /// </summary>
        /// <exception cref="MrpReRunBlockedException">有工單已投產，不可覆蓋（錯誤碼 WO_IN_PROGRESS）。</exception>
        Task<MrpRunResult> ReRunAsync(string mrpRunId, CancellationToken ct = default);

        /// <summary>
        /// 重跑前置可行性評估（mfg-domain.md §10-5 分級閘門）：唯讀掃描下游工單／派工／
        /// 委外需求／請購單的消化狀態，回傳可執行等級與逐項理由，不異動任何資料。
        /// 供畫面在使用者按下重跑之前預告後果，並由 <see cref="ReRunAsync"/> 作為唯一守門依據。
        /// </summary>
        Task<MrpReRunEvaluation> EvaluateReRunAsync(string mrpRunId, CancellationToken ct = default);

        /// <summary>
        /// 封存已展開的 MRP 執行單（mfg-domain.md §10-5 L3）：下游已有實體／財務足跡而無法重跑時，
        /// 宣告此次展開不再維護。
        ///
        /// **不連鎖作廢下游單據**——已投產工單、已收貨採購單、已轉委外單保持原狀，
        /// 各自走既有的沖銷／結案流程；本動作只作廢「完全未投產」的生產批與其工單，
        /// 讓那部分未滿足的需求回到可匯入清單，由新的執行單接手（閉環）。
        /// </summary>
        /// <returns>被作廢的生產批號。</returns>
        Task<IReadOnlyList<string>> ArchiveAsync(string mrpRunId, CancellationToken ct = default);
    }
}
