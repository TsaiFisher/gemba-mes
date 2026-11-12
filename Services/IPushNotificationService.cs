namespace SmartFactory.MES.Services
{
    public interface IPushNotificationService
    {
        /// <summary>推送給指定使用者的所有訂閱裝置。</summary>
        Task SendToUserAsync(string userId, string title, string body, string? url = null);

        /// <summary>推送給指定角色的所有訂閱裝置。</summary>
        Task SendToRoleAsync(string roleId, string title, string body, string? url = null);

        /// <summary>
        /// 依「收件人」語系產生內容後推送：<paramref name="compose"/> 會在切換成該收件人
        /// （<see cref="Models.ApplicationUser.Language"/>）的 CurrentUICulture 下被呼叫，
        /// 內容請在其中以資源檔取字。推播在背景服務或他人的請求中送出，當下執行緒的語系
        /// 不是收件人的，所以系統通知一律走這個多載。
        /// </summary>
        Task SendToUserAsync(string userId, Func<PushMessage> compose, string? url = null);

        /// <inheritdoc cref="SendToUserAsync(string, Func{PushMessage}, string?)"/>
        Task SendToRoleAsync(string roleId, Func<PushMessage> compose, string? url = null);
    }

    public readonly record struct PushMessage(string Title, string Body);
}
