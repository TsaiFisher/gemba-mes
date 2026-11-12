using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public interface IMonthEndService
    {
        /// <summary>初始化月結 Checklist（10 項標準步驟）。</summary>
        Task<MonthEndChecklist> InitChecklistAsync(string periodId, string plantId, CancellationToken ct = default);

        /// <summary>標記單一步驟完成。</summary>
        Task CompleteStepAsync(string checklistId, int stepNo, string userId, string? notes = null, CancellationToken ct = default);

        /// <summary>跳過單一步驟（需理由）。</summary>
        Task SkipStepAsync(string checklistId, int stepNo, string userId, string reason, CancellationToken ct = default);

        /// <summary>取得月結 Checklist（含步驟）。</summary>
        /// <summary>
        /// 取消月結：把期間從 Closing 退回可過帳狀態，並將步驟 8（封鎖庫存異動）退回未完成。
        ///
        /// 用在「步驟 8 打勾之後才發現關帳前稽核不過」——那些異常幾乎都得靠過帳去修，
        /// 而 Closing 正好把過帳擋住了。少了這個入口，期間會永遠停在月結中。
        /// </summary>
        Task CancelClosingAsync(string checklistId, string userId, CancellationToken ct = default);

        /// <summary>
        /// 以「期間＋廠別」取消月結，清單不存在也能取消。
        ///
        /// 期間可以不經月結清單就進入 Closing——「會計期間」畫面上的〈開始月結〉直接呼叫
        /// <c>StartClosingAsync</c>。走那條路進來時根本沒有清單，而 CompleteStep(8) 又會因為
        /// 期間已是 Closing 而拋錯，於是〈取消月結〉那顆鈕（條件是步驟 8 已完成）永遠不會出現。
        /// 見 W4-F8。
        /// </summary>
        Task CancelClosingByPeriodAsync(string periodId, string plantId, string userId, CancellationToken ct = default);

        Task<MonthEndChecklist?> GetChecklistAsync(string periodId, string plantId, CancellationToken ct = default);
    }
}
