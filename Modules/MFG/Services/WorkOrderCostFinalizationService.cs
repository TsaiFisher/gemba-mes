using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單成本月末結算實作。將該期 Preliminary 工單成本鎖定為 Final（不重算費率）。
    /// 使用 IgnoreQueryFilters + 顯式 plantId，讓月結可由任一廠別情境針對指定廠別結算。
    /// </summary>
    public class WorkOrderCostFinalizationService : IWorkOrderCostFinalizationService
    {
        private readonly ApplicationDbContext _db;
        private readonly IAccountingPeriodService _periods;

        public WorkOrderCostFinalizationService(
            ApplicationDbContext db, IAccountingPeriodService periods)
        {
            _db = db;
            _periods = periods;
        }

        public async Task<int> FinalizeAsync(string periodId, string plantId, CancellationToken ct = default)
        {
            var period = await _periods.GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_AccountingPeriodNotFoundFmt", plantId, periodId));
            if (period.Status == PeriodStatus.Closed)
                throw new InvalidOperationException(PzText.Get("Mfg_PeriodClosedNoFinalizeFmt", plantId, periodId));

            var costs = await _db.WorkOrderActualCosts
                .IgnoreQueryFilters()
                .Where(c => c.PlantId == plantId
                         && c.Status == CostStatus.Preliminary
                         && c.CostDate >= period.StartDate
                         && c.CostDate <= period.EndDate)
                .ToListAsync(ct);

            foreach (var c in costs)
                c.Status = CostStatus.Final;

            if (costs.Count > 0)
                await _db.SaveChangesAsync(ct);

            return costs.Count;
        }
    }
}
