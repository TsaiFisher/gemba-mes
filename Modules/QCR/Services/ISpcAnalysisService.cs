using SmartFactory.MES.Modules.QCR.Models;

namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>SPC 製程能力分析：由檢驗計量值計算 Cp/Cpk/管制圖趨勢。</summary>
    public interface ISpcAnalysisService
    {
        /// <param name="plantId">廠別（明確傳入，不依賴 HTTP 環境，便於整合測試）。</param>
        /// <param name="productCode">限定品號（null = 全部；填入時附管制圖序列）。</param>
        /// <param name="from">量測起日（含）。</param>
        /// <param name="to">量測迄日（含）。</param>
        Task<SpcAnalysisReport> BuildAsync(
            string plantId, string? productCode, DateOnly from, DateOnly to,
            CancellationToken ct = default);
    }
}
