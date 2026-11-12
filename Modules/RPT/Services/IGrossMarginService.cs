using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IGrossMarginService
    {
        Task<GrossMarginReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, CancellationToken ct = default);
    }
}
