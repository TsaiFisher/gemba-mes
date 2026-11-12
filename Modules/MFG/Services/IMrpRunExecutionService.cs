using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// MRP 執行單的「展開 → 採購需求落地為請購單」編排（mfg-domain.md §11 落地③ / §25.5）。
    ///
    /// 原本整段寫在 <c>MrpRunController.Edit</c> 與 <c>ReRun</c> 內，只有走畫面才跑得到。
    /// 抽出的理由是 §25.5 需要第二個呼叫端（訂單核准後自動展開）——
    /// 交易包覆、請購單去重／對帳、預檢錯誤回填這幾件事若讓第二個呼叫端各寫一份，
    /// 兩條路徑的 MRP 結果會慢慢分歧。
    ///
    /// 例外處理契約：一律回滾後**原樣往外拋**，由呼叫端決定呈現；
    /// 但 <c>MrpExpansionValidationException</c> 的逐列錯誤已在服務內回填 <c>ValidationError</c>。
    /// </summary>
    public interface IMrpRunExecutionService
    {
        /// <summary>首次展開（執行單須為 Draft）。</summary>
        Task<MrpRunResult> ExecuteAsync(string mrpRunId, CancellationToken ct = default);

        /// <summary>重跑（§10-5）：清除前次未投產展開後重新展開。</summary>
        Task<MrpRunResult> ReRunAsync(string mrpRunId, CancellationToken ct = default);
    }
}
