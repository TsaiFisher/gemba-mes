using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 製造簡化模式（mfg-domain.md §25）的**單一判定來源**。
    ///
    /// 刻意不讓各 controller 自己讀 <c>mfg_simplified_mode</c> 再各寫一份狀態清單——
    /// §26 的 FPI 閘口就是因為 Web 與 Mobile 各寫一份而漂移（一端擋、另一端不擋）。
    /// 開工放行與待辦清單這兩件事同樣有 Web／Mobile 兩條入口，故一開始就收斂於此。
    /// </summary>
    /// <remarks>
    /// ⚠️ 三個方法都**必須明確帶 <c>plantId</c>**（plant-scoped-system-params.md §3.3）：
    /// <c>mfg_simplified_mode</c> 已可依廠別覆寫，而本服務同時被請求期（DispatchController、
    /// 手機 API）與非請求期路徑呼叫。若讓服務自己去猜當前廠別，無 HTTP 上下文的呼叫會靜默
    /// 退回全域值——不拋例外、行為悄悄變。故由呼叫端負責提供，靠型別擋。
    /// </remarks>
    public interface IMfgSimplifiedModeService
    {
        /// <summary>簡化模式是否開啟。未設定（乾淨部署）一律為 false＝完整流程。</summary>
        Task<bool> IsEnabledAsync(string? plantId, CancellationToken ct = default);

        /// <summary>
        /// 此派工狀態是否允許開工。完整模式＝<c>Scheduled/ReadyToStart/InProgress</c>；
        /// 簡化模式額外放行 <c>Pending</c>——免排程後派工不會被推進 <c>Scheduled</c>，
        /// 現場直接從 MRP 展開建好的 <c>Pending</c> 派工開工。
        /// </summary>
        Task<bool> CanStartSessionAsync(DispatchStatus status, string? plantId, CancellationToken ct = default);

        /// <summary>
        /// 現場待辦清單（手機派工清單）應顯示的派工狀態。
        /// 簡化模式若不納入 <c>Pending</c>，即使開工放行了，派工在待辦上根本不會出現。
        /// </summary>
        Task<IReadOnlyList<DispatchStatus>> ActionableDispatchStatusesAsync(string? plantId, CancellationToken ct = default);
    }
}
