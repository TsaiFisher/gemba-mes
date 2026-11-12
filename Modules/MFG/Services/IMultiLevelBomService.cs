using SmartFactory.MES.Modules.MFG.ViewModels;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 多階展開服務：自指定品號起，沿 Product.RoutingId →（有效途程）→ 工序 → 工序用料
    /// 遞迴展開；用料若本身為自製件（有有效途程）則續往下一階。
    /// 供「多階料表」與「多階途程」兩畫面共用同一次展開結果。
    /// </summary>
    public interface IMultiLevelBomService
    {
        /// <summary>
        /// 展開多階料表/途程。today 用於途程生效區間判斷（EffectiveFrom/To）；
        /// 廠別隔離由 ApplicationDbContext 全域過濾器處理。
        /// </summary>
        Task<MultiLevelExplosionResult> ExplodeAsync(
            string productCode, DateOnly today, CancellationToken ct = default);
    }
}
