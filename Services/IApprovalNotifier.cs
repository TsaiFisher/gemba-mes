using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 簽核事件通知擴充點：送審／步驟推進／重新指派時通知當前簽核人，終局時通知申請人。
    /// Core 不內建任何通知管道；未註冊實作時 ApprovalService 以 <see cref="NullApprovalNotifier"/> 靜默略過。
    /// 部署專案（如 ProjectZ）可註冊自己的實作接上推播/信件等管道。
    /// 通知一律 best-effort：實作拋出例外不會中斷簽核流程（由 ApprovalService 捕捉記 log）。
    /// </summary>
    public interface IApprovalNotifier
    {
        /// <summary>
        /// 輪到新的簽核步驟（送審建立首步驟、核准後推進下一步、管理員重新指派）。
        /// step 為當前待簽步驟；目標簽核人依 request.ActiveApproverId（優先）或 step 的 Role/User 設定解析。
        /// </summary>
        Task NotifyPendingAsync(ApprovalRequest request, ApprovalStep step);

        /// <summary>簽核終局（核准／駁回／退回修改），通知申請人 request.RequesterId。</summary>
        Task NotifyFinalizedAsync(ApprovalRequest request, ApprovalStatus finalStatus);
    }

    /// <summary>預設空實作：未註冊通知管道時使用。</summary>
    public sealed class NullApprovalNotifier : IApprovalNotifier
    {
        public static readonly NullApprovalNotifier Instance = new();

        public Task NotifyPendingAsync(ApprovalRequest request, ApprovalStep step) => Task.CompletedTask;

        public Task NotifyFinalizedAsync(ApprovalRequest request, ApprovalStatus finalStatus) => Task.CompletedTask;
    }
}
