namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>投產（Release）結果分類（mfg-domain.md §24）。</summary>
    public enum WorkOrderReleaseOutcome
    {
        Released,        // 已釋出（直接核准）
        Submitted,       // 已送審（PendingApproval）
        NeedsConfirm,    // 重算量 ≠ 暫定量，待人工確認（§24.2）
        MaterialBlocked, // 硬料閘擋住：缺料且在途補不足（§24.3）
        NotPlanned,      // 非 Planned 狀態，不可投產
        NotNeeded,       // 重算量為 0：投產當下可用量已滿足毛需求，不需生產（不得投產，§24.2）
        NotFound,        // 查無工單
        ReleasedNoApprovalFlow // 已釋出（直接核准），但符合送審條件卻沒有簽核流程可送——呼叫端須警示（§18.2，A3）
    }

    public static class WorkOrderReleaseOutcomeExtensions
    {
        /// <summary>工單已釋出（不論是否附警示）。</summary>
        public static bool IsReleased(this WorkOrderReleaseOutcome o) =>
            o is WorkOrderReleaseOutcome.Released or WorkOrderReleaseOutcome.ReleasedNoApprovalFlow;
    }

    /// <summary>單一工單投產試算／結果（mfg-domain.md §24.2 / §24.3）。</summary>
    public sealed record WorkOrderReleasePreview(
        string WorkOrderId,
        WorkOrderReleaseOutcome Outcome,
        decimal PlannedQty,        // 暫定量（投產前 Quantity）
        decimal RecalculatedQty,   // net-at-release 重算後 Quantity
        bool MaterialRisk,         // 在途已鎖、貨未到但預計準時（§24.7）
        IReadOnlyList<string> ShortageProducts);  // 缺料品號（Outcome=MaterialBlocked 時）

    /// <summary>整批投產（all-or-nothing）結果分類（mfg-domain.md §24.1）。</summary>
    public enum BatchReleaseOutcome
    {
        Released,         // 全數定案
        NothingToRelease, // 批內無 Planned 工單
        MaterialBlocked,  // 任一工單缺料 → 整批不投
        NeedsConfirm,     // 有重算量變動待人工確認
        NotFound          // 查無生產批
    }

    public sealed record BatchReleaseBlockedLine(string WorkOrderId, IReadOnlyList<string> Shortages);
    public sealed record BatchReleaseConfirmLine(string WorkOrderId, decimal Planned, decimal Recalculated);

    /// <summary>整批投產試算／結果。</summary>
    public sealed record BatchReleaseResult(
        BatchReleaseOutcome Outcome,
        int Released,
        int Submitted,
        IReadOnlyList<BatchReleaseBlockedLine> Blocked,
        IReadOnlyList<BatchReleaseConfirmLine> NeedsConfirm,
        IReadOnlyList<string>? NotNeeded = null,   // 重算為 0 而略過的工單（含其下階子工單）
        IReadOnlyList<string>? NoApprovalFlow = null)   // 本該送審卻沒有簽核流程、已直接核准的工單（A3）
    {
        public IReadOnlyList<string> UnapprovedByFlow => NoApprovalFlow ?? Array.Empty<string>();
        public IReadOnlyList<string> Skipped => NotNeeded ?? Array.Empty<string>();
    }

    /// <summary>
    /// 投產定案服務（mfg-domain.md §24）：把 WorkOrderController.Release 的「重算量 → 硬料閘（含在途鎖）
    /// → 鎖庫出 → 軟預留轉鎖」核心邏輯抽出，供單工單投產與批層整批投產共用。
    /// </summary>
    public interface IWorkOrderReleaseService
    {
        /// <summary>
        /// 投產試算（不異動資料）：回傳重算量與料況閘口結果，供前端「暫定 → 重算」人工確認
        /// 與批層 all-or-nothing 預檢使用。
        /// </summary>
        Task<WorkOrderReleasePreview> PreviewAsync(string workOrderId, CancellationToken ct = default);

        /// <summary>
        /// 實際投產（定案）：重算量 → 硬料閘（含在途 peg）→ 鎖庫出 → 標準成本快照 → 送審/核准 → Released。
        /// 若重算量 ≠ 暫定量且 confirmed=false，回傳 NeedsConfirm 不異動；料閘不過回傳 MaterialBlocked。
        /// 不在內部呼叫最終 SaveChanges 以外的交易控制（相容 InMemory 測試）。
        /// </summary>
        Task<WorkOrderReleasePreview> ApplyAsync(string workOrderId, bool confirmed, CancellationToken ct = default);

        /// <summary>
        /// 簡化模式自動投產（mfg-domain.md §25.3）：MRP 展開後直接把工單推到 Released。
        /// 保留在途 peg、標準成本快照、Push 料領料單、委外自動建 PO；
        /// 跳過淨量重算、硬料閘、簽核判定。不會回傳 NeedsConfirm/MaterialBlocked（缺料不擋單，
        /// 改由開工時的 mfg_material_gate 軟閘接手），缺料品號仍以 ShortageProducts 回報供記錄。
        /// </summary>
        Task<WorkOrderReleasePreview> AutoReleaseAsync(string workOrderId, CancellationToken ct = default);

        /// <summary>
        /// 整批投產（mfg-domain.md §24.1 all-or-nothing）：批內所有 Planned 工單一起投產。
        /// 先逐張試算——任一缺料 → 回 MaterialBlocked 整批不投；任一重算量變動且 confirmed=false
        /// → 回 NeedsConfirm 待確認；全數通過才逐張定案（半成品先於成品）。
        /// </summary>
        Task<BatchReleaseResult> ReleaseBatchAsync(string batchId, bool confirmed, CancellationToken ct = default);
    }
}
