using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 下工（ProductionSession Closed）時累計實際人工/機台成本（mfg-domain.md §16.3）。
    /// Web（DispatchController）與 Mobile（MobileMfgController）共用同一份邏輯，
    /// 避免各自實作日後改一邊漏一邊。
    /// </summary>
    public interface IProductionCostAccrualService
    {
        /// <summary>
        /// <paramref name="closedSession"/> 需已設好 <c>EndAt</c>/<c>Status=Closed</c>（尚未 SaveChanges 皆可），
        /// 呼叫端仍須自行 SaveChangesAsync。
        /// </summary>
        Task AccrueOnSessionStopAsync(ProductionSession closedSession);

        /// <summary>
        /// 備料完成（ProductionSetup 全數 Closed）時累計準備工時成本：
        /// <list type="bullet">
        /// <item>人工：<b>逐人</b>各自的備料時長 × WorkCenter.StandardLaborCost —— 備料人員與生產人員
        /// 可能不同人，這段人工原本完全沒入帳。</item>
        /// <item>製造費用：依「最早開始備料 → 完成」的<b>時間窗結算一次</b>，不隨備料人數疊加
        /// （與下工 overhead 防灌水同一原則）。</item>
        /// </list>
        /// <paramref name="closedSetups"/> 需已設好 <c>SetupEndAt</c>（尚未 SaveChanges 皆可），
        /// 呼叫端仍須自行 SaveChangesAsync。
        /// </summary>
        Task AccrueOnSetupDoneAsync(IReadOnlyList<ProductionSetup> closedSetups, Dispatch dispatch);
    }
}
