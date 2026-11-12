using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IDashboardService
    {
        Task<DashboardKpi> GetKpiAsync(string plantId, CancellationToken ct = default);
    }
}
