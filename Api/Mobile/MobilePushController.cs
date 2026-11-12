using SmartFactory.MES.Modules._Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>Web Push 訂閱管理 API（mobile-pwa.md §9 Phase E5）。</summary>
    [Route("api/mobile/push")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobilePushController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly IConfiguration _config;
        private readonly IPushNotificationService _push;

        public MobilePushController(
            ApplicationDbContext db,
            IUserContext userContext,
            IConfiguration config,
            IPushNotificationService push)
        {
            _db          = db;
            _userContext = userContext;
            _config      = config;
            _push        = push;
        }

        /// <summary>回傳 VAPID 公鑰（前端訂閱用）。</summary>
        [HttpGet("vapid-key")]
        [AllowAnonymous]
        public IActionResult GetVapidKey()
        {
            var key = _config["WebPush:VapidPublicKey"] ?? "";
            return Ok(new { publicKey = key });
        }

        /// <summary>訂閱 Web Push（註冊或更新裝置訂閱）。</summary>
        [HttpPost("subscribe")]
        [RequestSizeLimit(8 * 1024)]
        [EnableRateLimiting("pushRateLimit")]
        [PermissionGateExempt("推播訂閱綁在登入者自己身上（UserId＝目前使用者），不涉業務資料；" +
            "跨帳號搶綁另有下方金鑰比對防護")]
        public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest req)
        {
            if (string.IsNullOrEmpty(req.Endpoint) || string.IsNullOrEmpty(req.P256dh) || string.IsNullOrEmpty(req.Auth))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_SUBSCRIPTION", PzText.Get("Mob_SubscriptionFieldsMissing"))));

            if (!new PushEndpointPolicy(_config).IsAllowed(req.Endpoint) ||
                !ValidKey(req.P256dh, 65) || !ValidKey(req.Auth, 16) ||
                req.UserAgent?.Length > 512)
                return BadRequest(new MobileErrorResult(Error: new("INVALID_SUBSCRIPTION", PzText.Get("Mob_SubscriptionInvalid"))));

            var existing = await _db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint);

            var maxSubscriptions = Math.Clamp(_config.GetValue("WebPush:MaxSubscriptionsPerUser", 10), 1, 100);
            if ((existing == null || existing.UserId != _userContext.UserId) &&
                await _db.PushSubscriptions.CountAsync(s => s.UserId == _userContext.UserId) >= maxSubscriptions)
                return BadRequest(new MobileErrorResult(Error: new("SUBSCRIPTION_LIMIT", PzText.Get("Mob_SubscriptionLimit"))));

            if (existing != null)
            {
                // 防訂閱劫持：既有訂閱屬於其他帳號時，僅允許持有相同金鑰
                // （即同一瀏覽器的原訂閱，常見於共用裝置換人登入）改綁；
                // 只知道 Endpoint 的第三方無法奪走通知通道。
                bool sameUser = existing.UserId == _userContext.UserId;
                bool sameKeys = FixedTimeEquals(existing.P256dhKey, req.P256dh)
                             && FixedTimeEquals(existing.AuthKey, req.Auth);
                if (!sameUser && !sameKeys)
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new MobileErrorResult(Error: new("SUBSCRIPTION_CONFLICT",
                            PzText.Get("Mob_SubscriptionOwnedByOther"))));

                existing.UserId    = _userContext.UserId;
                existing.P256dhKey = req.P256dh;
                existing.AuthKey   = req.Auth;
                existing.UserAgent = req.UserAgent;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.PushSubscriptions.Add(new PushSubscriptionRecord
                {
                    UserId    = _userContext.UserId,
                    Endpoint  = req.Endpoint,
                    P256dhKey = req.P256dh,
                    AuthKey   = req.Auth,
                    UserAgent = req.UserAgent,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return Ok(new MobileOkResult(Message: PzText.Get("Mob_Subscribed")));
        }

        /// <summary>
        /// 測試推播：推送一則測試通知給目前登入使用者的所有訂閱裝置。
        /// 用途：驗證 VAPID 金鑰、Service Worker push 事件與手機通知顯示。
        /// </summary>
        [HttpPost("test")]
        [RequestSizeLimit(8 * 1024)]
        [EnableRateLimiting("pushRateLimit")]
        [PermissionGateExempt("只推給自己的裝置（SendToUserAsync(目前使用者)），不會外流任何資料")]
        public async Task<IActionResult> SendTest([FromBody] PushTestRequest req)
        {
            if (req.Title?.Length > 200 || req.Body?.Length > 2000 ||
                (req.Url != null && !Url.IsLocalUrl(req.Url)))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_REQUEST", PzText.Get("Mob_SubscriptionInvalid"))));

            var userId = _userContext.UserId;
            var count  = await _db.PushSubscriptions.CountAsync(s => s.UserId == userId);
            if (count == 0)
                return BadRequest(new MobileErrorResult(Error: new("NO_SUBSCRIPTION", PzText.Get("Mob_NotSubscribed"))));

            await _push.SendToUserAsync(
                userId,
                title: req.Title ?? PzText.Get("Mob_PushTestTitle"),
                body:  req.Body  ?? PzText.Get("Mob_PushTestBodyFmt", DateTime.Now),
                url:   req.Url   ?? "/mobile/home.html");

            return Ok(new MobileOkResult(Message: PzText.Get("Mob_PushTestSentFmt", count)));
        }

        /// <summary>查詢目前使用者的訂閱裝置數量。</summary>
        [HttpGet("status")]
        [PermissionGateExempt("只查自己的訂閱裝置（WHERE UserId = 目前使用者）")]
        public async Task<IActionResult> GetStatus()
        {
            var userId = _userContext.UserId;
            var subs   = await _db.PushSubscriptions.AsNoTracking()
                .Where(s => s.UserId == userId)
                .Select(s => new { s.UserAgent, s.UpdatedAt })
                .ToListAsync();
            return Ok(new MobileOkResult(Data: new { subscriptionCount = subs.Count, devices = subs }));
        }

        /// <summary>取消訂閱（裝置登出或使用者手動關閉）。</summary>
        [HttpPost("unsubscribe")]
        [PermissionGateExempt("只刪自己的訂閱（WHERE Endpoint = ? AND UserId = 目前使用者）")]
        public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeRequest req)
        {
            if (string.IsNullOrEmpty(req.Endpoint))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_REQUEST", PzText.Get("Mob_EndpointMissing"))));

            var sub = await _db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint && s.UserId == _userContext.UserId);

            if (sub != null)
            {
                _db.PushSubscriptions.Remove(sub);
                await _db.SaveChangesAsync();
            }

            return Ok(new MobileOkResult(Message: PzText.Get("Mob_Unsubscribed")));
        }

        private static bool ValidKey(string value, int length)
        {
            try
            {
                var normalized = value.Replace('-', '+').Replace('_', '/');
                normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
                var bytes = Convert.FromBase64String(normalized);
                return bytes.Length == length && (length != 65 || bytes[0] == 4);
            }
            catch (FormatException) { return false; }
        }

        /// <summary>定時比較，避免以回應時間差猜出既有訂閱金鑰。</summary>
        private static bool FixedTimeEquals(string? a, string? b)
            => a != null && b != null
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                      System.Text.Encoding.UTF8.GetBytes(a),
                      System.Text.Encoding.UTF8.GetBytes(b));
    }

    public record SubscribeRequest(string Endpoint, string P256dh, string Auth, string? UserAgent);
    public record UnsubscribeRequest(string Endpoint);
    public record PushTestRequest(string? Title, string? Body, string? Url);
}
