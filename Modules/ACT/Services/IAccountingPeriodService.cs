using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 會計期間服務。**每個方法都要帶 plantId**：期間自 2026-08-30 起依廠別分開
    /// （manual-test-plan §12.4 #2／#3），一個廠關帳不再把全公司的帳一起關掉。
    ///
    /// plantId 一律由呼叫端傳入單據所屬廠別，**不是**操作者當下切在哪個廠——
    /// 跨廠調撥收貨時兩者本來就不同（2026-08-17 裁決：跨廠以入庫端為準）。
    /// 實作端因此全走 <c>IgnoreQueryFilters()</c>，不依賴全域廠別過濾器。
    /// </summary>
    public interface IAccountingPeriodService
    {
        /// <summary>取得該廠指定日期所屬的期間，找不到則回傳 null。</summary>
        Task<AccountingPeriod?> GetByDateAsync(DateOnly date, string plantId, CancellationToken ct = default);

        /// <summary>取得該廠指定 PeriodId 的期間。</summary>
        Task<AccountingPeriod?> GetByIdAsync(string periodId, string plantId, CancellationToken ct = default);

        /// <summary>
        /// 驗證該廠的該日期是否可過帳（期間為 Open 或 Reopened）；不可過帳則拋 InvalidOperationException。
        /// 別廠的期間狀態不影響本廠。
        /// </summary>
        Task AssertCanPostAsync(DateOnly movementDate, string plantId, CancellationToken ct = default);

        /// <summary>將該廠的期間狀態推進到 Closing（開始月結）。</summary>
        Task StartClosingAsync(string periodId, string plantId, string userId, CancellationToken ct = default);

        /// <summary>完成該廠的月結，將期間狀態設為 Closed。</summary>
        Task ClosePeriodAsync(string periodId, string plantId, string userId, CancellationToken ct = default);

        /// <summary>
        /// 取消月結，把 Closing 退回可過帳狀態（曾關過帳的回 Reopened，否則回 Open）。
        ///
        /// 沒有這個出口的話 Closing 是個死狀態：關帳要稽核過，而稽核異常多半得靠過帳去修
        /// （補開票、補認列、沖銷、調整庫存），過帳又被 Closing 擋著，於是期間永遠停在月結中，
        /// 現場唯一的辦法是直接改資料庫。
        /// </summary>
        Task CancelClosingAsync(string periodId, string plantId, string userId, CancellationToken ct = default);

        /// <summary>重開帳（需雙重核准後呼叫）。</summary>
        Task ReopenPeriodAsync(string periodId, string plantId, string userId, CancellationToken ct = default);

        /// <summary>為指定廠別建立下一個月份的期間（自動計算 StartDate / EndDate）。</summary>
        Task<AccountingPeriod> CreateNextPeriodAsync(string plantId, CancellationToken ct = default);
    }
}
