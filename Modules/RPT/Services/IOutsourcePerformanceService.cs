using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IOutsourcePerformanceService
    {
        Task<OutsourcePerformanceReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, CancellationToken ct = default);
    }
}
