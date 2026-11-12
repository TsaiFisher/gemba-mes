using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// <see cref="IFpiGateService"/> 實作（mfg-domain.md §26.2）。
    /// </summary>
    public class FpiGateService : IFpiGateService
    {
        private readonly ApplicationDbContext _db;

        public FpiGateService(ApplicationDbContext db) => _db = db;

        public async Task<FpiGateResult> EvaluateAsync(Dispatch dispatch, CancellationToken ct = default)
        {
            var requiresFpi = await _db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == dispatch.WorkOrderId
                         && o.OperationSeq == dispatch.OperationSeq)
                .Select(o => (bool?)o.RequiresFpi)
                .FirstOrDefaultAsync(ct) ?? false;

            // 工序不要求 FPI 就不必查檢驗單（絕大多數派工走這條）
            if (!requiresFpi) return new FpiGateResult(false, false);

            var hasPassed = await HasPassedFpiAsync(dispatch.DispatchId, ct);
            return new FpiGateResult(true, hasPassed);
        }

        public void ApplyResult(Dispatch dispatch, bool passed, DateTime now)
        {
            if (!passed)
            {
                // 經 DispatchHold 才會記下原因：恢復時回到等待首件檢查、重新量測
                DispatchHold.Hold(dispatch, DispatchHoldReason.FpiFailed, now);
                return;
            }
            dispatch.Status    = DispatchStatus.ReadyToStart;
            dispatch.UpdatedAt = now;
        }

        /// <summary>
        /// 是否已有判定 Pass 的 FPI 檢驗單。
        /// <c>OrderType</c> 為 Phase G 才加入的欄位，早期資料可能只有 <c>InspectionType</c> 字串，
        /// 故兩者皆比對——漏掉任一種都會讓已通過首件的派工被誤擋。
        /// </summary>
        private Task<bool> HasPassedFpiAsync(string dispatchId, CancellationToken ct) =>
            _db.InspectionOrders.AsNoTracking()
                .AnyAsync(o => o.SourceDocId == dispatchId
                            && o.Status == "Pass"
                            && (o.OrderType == InspectionOrderType.FPI || o.InspectionType == "FPI"), ct);
    }
}
