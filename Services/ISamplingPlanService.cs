using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 抽樣計畫決策服務（MIL-STD-1916 VL 切換 + 選配跳批）。
    /// 依 InspectionSpec 的歷史合格率決定本次有效 VL 與是否跳批，
    /// 並在檢驗完成後更新規格的連續合格批數與狀態。
    /// </summary>
    public interface ISamplingPlanService
    {
        /// <summary>
        /// 取得本批的抽樣決策。
        /// 若 ShouldInspect = false，表示跳批，呼叫端可跳過建立 InspectionOrder。
        /// </summary>
        Task<SamplingDecision> GetDecisionAsync(
            string?  productCode,
            string?  supplierCode,
            string   inspectionType,
            decimal  lotSize,
            string?  plantId,
            string?  machineCode = null,
            string?  toolingCode = null,
            CancellationToken ct = default);

        /// <summary>
        /// 記錄一批檢驗結果，依 MIL-STD-1916 切換規則更新規格狀態。
        /// machineCode / toolingCode 供 SamplingScope = MachineLevel / ToolingLevel 分區計數。
        /// 應在 InspectionOrder 提交（Pass/Fail）後呼叫。
        /// </summary>
        Task RecordResultAsync(
            string?  productCode,
            string?  supplierCode,
            string?  plantId,
            bool     passed,
            string?  machineCode = null,
            string?  toolingCode = null,
            CancellationToken ct = default);
    }

    /// <summary>抽樣計畫決策結果。</summary>
    public record SamplingDecision(
        bool   ShouldInspect,
        int    VerificationLevel,
        int    SampleSize,
        string SamplingState,
        string? SkipReason = null);
}
