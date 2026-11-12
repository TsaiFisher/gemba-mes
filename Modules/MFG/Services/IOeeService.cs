using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>OEE（設備綜合效率）報表：依機台彙總可用率／表現性／良率。</summary>
    public interface IOeeService
    {
        /// <param name="plantId">廠別（明確傳入，不依賴 HTTP 環境，便於整合測試）。</param>
        /// <param name="machineCode">限定機台（null = 全部）。</param>
        Task<OeeReport> BuildAsync(
            string plantId, DateOnly from, DateOnly to, string? machineCode,
            CancellationToken ct = default);
    }
}
