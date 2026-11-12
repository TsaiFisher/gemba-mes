using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// IApprovalNotifier 的 ProjectZ 實作：以 Web Push 通知簽核事件。
    /// 目標解析與「待我簽核」判定語意一致：
    /// ActiveApproverId 已指派（主管解析/重新指派）→ 推該使用者；
    /// 未指派 → 依步驟 Role/User 目標推播；Supervisor 未解析 → 無對象
    /// （由「所有簽核記錄」的未指派篩選讓管理員介入，不在此補救）。
    /// ApprovalService 以 best-effort 呼叫本類別，推播失敗不影響簽核流程。
    /// </summary>
    public class PushApprovalNotifier : IApprovalNotifier
    {
        private readonly IPushNotificationService _push;

        public PushApprovalNotifier(IPushNotificationService push) => _push = push;

        public async Task NotifyPendingAsync(ApprovalRequest request, ApprovalStep step)
        {
            // 內容依收件人語系產生（推播在簽核者以外的人的請求中送出，當下語系不是收件人的）
            PushMessage Compose() => new(PzText.Get("Push_ApprovalPendingTitle"),
                PzText.Get("Push_ApprovalPendingBodyFmt", request.DocumentType, request.DocumentId));
            // 🚨 **推播的落點一定要是手機路由。** Web Push 的訂閱只在
            // wwwroot/mobile/home.page.js 建立（全站沒有第二個 pushManager.subscribe），
            // 所以**每一則推播的收件端都是手機 PWA**。這裡原本指向桌機的
            // /Approval/PendingIndex，而 sw.js 的 notificationclick 會對現有的 /mobile/
            // 視窗做 client.navigate(url)——等於把 PWA 從手機殼帶到桌機頁，
            // 而 standalone 模式沒有網址列，使用者只剩 OS 手勢能回來。
            const string url = "/mobile/approval/list.html";

            if (!string.IsNullOrEmpty(request.ActiveApproverId))
                await _push.SendToUserAsync(request.ActiveApproverId, Compose, url);
            else if (step.ApproverType == ApproverType.Role && !string.IsNullOrEmpty(step.TargetRoleId))
                await _push.SendToRoleAsync(step.TargetRoleId, Compose, url);
            else if (step.ApproverType == ApproverType.User && !string.IsNullOrEmpty(step.TargetUserId))
                await _push.SendToUserAsync(step.TargetUserId, Compose, url);
        }

        public async Task NotifyFinalizedAsync(ApprovalRequest request, ApprovalStatus finalStatus)
        {
            if (string.IsNullOrEmpty(request.RequesterId)) return;

            await _push.SendToUserAsync(request.RequesterId, () =>
            {
                var label = finalStatus switch
                {
                    ApprovalStatus.Approved            => PzText.Get("Push_ApprovalApproved"),
                    ApprovalStatus.Rejected            => PzText.Get("Push_ApprovalRejected"),
                    ApprovalStatus.ReturnedForRevision => PzText.Get("Push_ApprovalReturned"),
                    _                                  => finalStatus.ToString()
                };
                return new PushMessage(PzText.Get("Push_ApprovalResultTitle"),
                    PzText.Get("Push_ApprovalResultBodyFmt", request.DocumentType, request.DocumentId, label));
            });
        }
    }
}
