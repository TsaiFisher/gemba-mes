using SmartFactory.MES.Modules.RPT.Models;
using static SmartFactory.MES.Modules.RPT.Services.ContributionMarginCalculator;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>產品邊際貢獻分析（路線 A：以工單成本要素推變動占比）。</summary>
    public interface IContributionMarginService
    {
        /// <param name="policyOverride">null = 用系統參數預設分類；否則以此次查詢覆寫。</param>
        Task<ContributionMarginReport> BuildAsync(
            DateOnly fromDate, DateOnly toDate, string plantId,
            VariablePolicy? policyOverride = null, CancellationToken ct = default);

        /// <summary>讀取系統參數中的變動成本分類預設（缺省：材料+人工＝變動、製費＝固定）。</summary>
        Task<VariablePolicy> GetDefaultPolicyAsync(CancellationToken ct = default);

        /// <summary>將變動成本分類寫入系統參數作為預設。</summary>
        Task SaveDefaultPolicyAsync(VariablePolicy policy, CancellationToken ct = default);
    }
}
