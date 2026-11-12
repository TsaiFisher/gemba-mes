using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>單一工作中心當月實際費用（費率重估分子）。</summary>
    public record DepartmentActualCostInput(decimal ActualLaborCost, decimal ActualOverheadCost);

    /// <summary>提供「各工作中心當月實際費用」作為實際費率重估的分子來源。
    /// 目前實作由月結手動輸入的 DepartmentActualCost 主檔讀取（可稽核、可重跑）；
    /// 未來成本中心標記到位後，可改以同介面的 GL 自動歸集 provider 換上，重估引擎不需異動。</summary>
    public interface IDepartmentActualCostProvider
    {
        /// <summary>取得指定期間 × 廠別各工作中心的當月實際費用（key = WorkCenterCode）。</summary>
        Task<IReadOnlyDictionary<string, DepartmentActualCostInput>> GetAsync(
            string periodId, string plantId, CancellationToken ct = default);
    }

    /// <summary>由 DepartmentActualCost 主檔讀取（月結手動輸入並持久化）。</summary>
    public class PersistedDepartmentActualCostProvider : IDepartmentActualCostProvider
    {
        private readonly ApplicationDbContext _db;

        public PersistedDepartmentActualCostProvider(ApplicationDbContext db) => _db = db;

        public async Task<IReadOnlyDictionary<string, DepartmentActualCostInput>> GetAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            var rows = await _db.DepartmentActualCosts
                .IgnoreQueryFilters()
                .Where(d => d.PeriodId == periodId && d.PlantId == plantId)
                .ToListAsync(ct);

            return rows.ToDictionary(
                d => d.WorkCenterCode,
                d => new DepartmentActualCostInput(d.ActualLaborCost, d.ActualOverheadCost));
        }
    }
}
