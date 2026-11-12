namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>期末實際費率重估結果摘要。</summary>
    public record ActualRateReestimationResult(
        int WorkOrdersRecosted,   // 回刷的完工工單數
        int WipSnapshots,         // 產生的 WIP 快照數
        decimal LaborAdjustment,  // 完工工單人工調整淨額（實際 - 原標準吸收）
        decimal OverheadAdjustment, // 完工工單製費調整淨額
        string? JournalId);       // 調整分錄號（無調整時為 null）

    /// <summary>工單實際費率期末重估（roadmap-fin-cst-sub.md §3.3 / §3.6）。
    /// 以各工作中心當月實際費用 ÷ 當月實際報工工時得「實際費率」，
    /// 回刷當月完工工單成本（Preliminary→Final）與在製品 WIP 快照，並產生差異調整分錄。</summary>
    public interface IActualRateReestimationService
    {
        Task<ActualRateReestimationResult> RunAsync(
            string periodId, string plantId, CancellationToken ct = default);
    }
}
