using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>多維度銷售毛利分析（客戶／品號／月份）。</summary>
    public interface IMarginAnalysisService
    {
        /// <param name="dimension">Customer / Product / Month（其餘視為 Product）。</param>
        Task<MarginAnalysisReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, string dimension,
            CancellationToken ct = default);
    }
}
