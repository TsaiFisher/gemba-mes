using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public interface IEclService
    {
        /// <summary>依帳齡矩陣計算 IFRS 9 ECL 呆帳準備，並儲存評估結果。</summary>
        Task<IList<EclAllowance>> ComputeEclAsync(
            string periodId, string plantId, DateOnly asOfDate,
            CancellationToken ct = default);

        /// <summary>
        /// 本廠適用的帳齡損失率（廠別設定 → 全公司設定 → 系統預設），依 AgingDaysFrom 排序。
        /// ECL 與呆帳提列共用這一份——兩支各寫一套損失率，正是它們算出兩個不同數字的原因。
        /// </summary>
        Task<IReadOnlyList<EclLossRate>> GetLossRatesAsync(string plantId, CancellationToken ct = default);
    }
}
