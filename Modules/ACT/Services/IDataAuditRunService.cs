using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 資料稽核執行與結果持久化（business_data_flow_reconciliation.md §9 P-8）。
    /// 包一層 IDataAuditService：執行全部檢核 → 寫入 DataAuditRun/DataAuditFinding →
    /// 清除逾保留期的舊紀錄，供每日排程與稽核儀表板共用。
    /// </summary>
    public interface IDataAuditRunService
    {
        /// <summary>執行全部稽核並持久化結果；plantId = null 稽核全廠。</summary>
        Task<DataAuditRun> RunAndPersistAsync(
            string? plantId, string triggerType, string createdBy, CancellationToken ct = default);
    }
}
