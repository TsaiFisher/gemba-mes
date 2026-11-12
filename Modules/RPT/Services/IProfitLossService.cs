using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IProfitLossService
    {
        /// <summary>單一廠別。等同於 <see cref="GetConsolidatedReportAsync"/> 傳一個廠。</summary>
        Task<ProfitLossReport> GetReportAsync(
            string fromPeriod, string toPeriod, string plantId, CancellationToken ct = default);

        /// <summary>
        /// 多廠合併。損益科目沒有廠際往來的對沖問題（跨廠調撥按成本搬、不加價，
        /// 所以不產生內部損益），直接加總即為合併損益。
        /// </summary>
        Task<ProfitLossReport> GetConsolidatedReportAsync(
            string fromPeriod, string toPeriod, IReadOnlyCollection<string> plantIds,
            CancellationToken ct = default);
    }
}
