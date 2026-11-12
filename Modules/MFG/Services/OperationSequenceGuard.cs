using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IOperationSequenceGuard"/>
    public class OperationSequenceGuard : IOperationSequenceGuard
    {
        private readonly ApplicationDbContext _db;
        private readonly IMfgSimplifiedModeService _simplified;

        public OperationSequenceGuard(ApplicationDbContext db, IMfgSimplifiedModeService simplified)
        {
            _db = db;
            _simplified = simplified;
        }

        public async Task<OperationSequenceCheck> CheckAsync(
            Dispatch dispatch, decimal qtyOk, CancellationToken ct = default)
        {
            var attempted = dispatch.QtyDone + qtyOk;

            // 完整模式：排程已保證順序，不加這道守門（加了會擋掉工序重疊等既有排法）
            if (!await _simplified.IsEnabledAsync(dispatch.PlantId, ct))
                return new OperationSequenceCheck(false, 0m, attempted);

            // 首道工序沒有前置可比
            if (string.IsNullOrEmpty(dispatch.PrevDispatchId))
                return new OperationSequenceCheck(false, 0m, attempted);

            var prevQtyDone = await _db.Dispatches.AsNoTracking()
                .Where(d => d.DispatchId == dispatch.PrevDispatchId)
                .Select(d => (decimal?)d.QtyDone)
                .FirstOrDefaultAsync(ct);

            // 查無前置派工（資料異常／已被刪除）：不擋，避免守門本身變成停產原因
            if (prevQtyDone == null)
                return new OperationSequenceCheck(false, 0m, attempted);

            return new OperationSequenceCheck(attempted > prevQtyDone.Value, prevQtyDone.Value, attempted);
        }
    }
}
