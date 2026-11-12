using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public interface IFxRevaluationService
    {
        /// <summary>IAS 21 期末匯率重評：對所有未結 AP/AR Invoice 以期末匯率重評。</summary>
        Task<IList<FxRevaluationEntry>> RunRevaluationAsync(
            string periodId, string plantId,
            IDictionary<string, decimal> periodEndRates,  // currencyCode → rate
            CancellationToken ct = default);
    }
}
