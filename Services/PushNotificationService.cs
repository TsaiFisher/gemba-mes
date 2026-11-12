using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using WebPush;

namespace SmartFactory.MES.Services
{
    public class PushNotificationService : IPushNotificationService, IDisposable
    {
        private readonly ApplicationDbContext _db;
        private readonly WebPushClient _client;
        private readonly HttpClient _http;
        private readonly PushEndpointPolicy _endpoints;
        private readonly VapidDetails _vapid;
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(
            ApplicationDbContext db,
            IConfiguration config,
            ILogger<PushNotificationService> logger)
        {
            _db     = db;
            _logger = logger;
            _endpoints = new PushEndpointPolicy(config);
            _http = _endpoints.CreateClient();
            _client = new WebPushClient();

            var section = config.GetSection("WebPush");
            _vapid = new VapidDetails(
                subject:    section["VapidSubject"]    ?? "mailto:admin@smartfactory.local",
                publicKey:  section["VapidPublicKey"]  ?? "",
                privateKey: section["VapidPrivateKey"] ?? "");
        }

        public void Dispose() => _http.Dispose();

        public Task SendToUserAsync(string userId, string title, string body, string? url = null)
            => SendAsync(_db.PushSubscriptions.Where(s => s.UserId == userId), title, body, url);

        public Task SendToRoleAsync(string roleId, string title, string body, string? url = null)
        {
            var userIds = _db.UserRoles
                .Where(ur => ur.RoleId == roleId)
                .Select(ur => ur.UserId);
            return SendAsync(_db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)), title, body, url);
        }

        public Task SendToUserAsync(string userId, Func<PushMessage> compose, string? url = null)
            => SendLocalizedAsync(_db.PushSubscriptions.Where(s => s.UserId == userId), compose, url);

        public Task SendToRoleAsync(string roleId, Func<PushMessage> compose, string? url = null)
        {
            var userIds = _db.UserRoles
                .Where(ur => ur.RoleId == roleId)
                .Select(ur => ur.UserId);
            return SendLocalizedAsync(_db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)), compose, url);
        }

        /// <summary>
        /// 依訂閱者的 Language 分組，每組在該語系下產生一次內容。語系只在同步呼叫 compose 的
        /// 這一段切換、立刻還原，不跨 await，避免汙染同一執行緒上的其他工作。
        /// 未設定語系者比照 RequestLocalization 的預設（zh-TW）。
        /// </summary>
        private async Task SendLocalizedAsync(
            IQueryable<Models.PushSubscriptionRecord> query, Func<PushMessage> compose, string? url)
        {
            var subs = await query.AsNoTracking().ToListAsync();
            if (subs.Count == 0) return;
            var userIds = subs.Select(s => s.UserId).Distinct().ToList();
            var langByUser = await _db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Language);

            foreach (var group in subs.GroupBy(s => CultureFor(langByUser.GetValueOrDefault(s.UserId))))
            {
                var previous = System.Globalization.CultureInfo.CurrentUICulture;
                PushMessage msg;
                try
                {
                    System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(group.Key);
                    msg = compose();
                }
                finally
                {
                    System.Globalization.CultureInfo.CurrentUICulture = previous;
                }
                await SendToSubscriptionsAsync(group.ToList(), msg.Title, msg.Body, url);
            }
        }

        internal static string CultureFor(string? language)
            => language != null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-TW";

        private async Task SendAsync(
            IQueryable<Models.PushSubscriptionRecord> query,
            string title, string body, string? url)
        {
            var subs = await query.AsNoTracking().ToListAsync();
            await SendToSubscriptionsAsync(subs, title, body, url);
        }

        private async Task SendToSubscriptionsAsync(
            List<Models.PushSubscriptionRecord> subs, string title, string body, string? url)
        {
            var stale = new List<string>();

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                title,
                body,
                url = url ?? "/mobile/home.html",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var sub in subs)
            {
                if (timeout.IsCancellationRequested)
                {
                    // 整批 30 秒上限：用完就停，剩下的不送，但要留下紀錄，否則看起來像全部送出。
                    _logger.LogWarning("Push batch time budget exhausted; {Remaining} subscriptions not attempted",
                        subs.Count - subs.IndexOf(sub));
                    break;
                }
                if (!_endpoints.IsAllowed(sub.Endpoint))
                {
                    _logger.LogWarning("Blocked untrusted stored push subscription {SubscriptionId}", sub.Id);
                    continue;
                }
                try
                {
                    var push = new PushSubscription(sub.Endpoint, sub.P256dhKey, sub.AuthKey);
                    using var request = _client.GenerateRequestDetails(push, payload,
                        new Dictionary<string, object> { ["vapidDetails"] = _vapid });
                    using var response = await _http.SendAsync(request, timeout.Token);
                    if (response.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
                        stale.Add(sub.Id);
                    else
                        response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "推送失敗: {Endpoint}", sub.Endpoint[..Math.Min(40, sub.Endpoint.Length)]);
                }
            }

            if (stale.Count > 0)
            {
                var toRemove = _db.PushSubscriptions.Where(s => stale.Contains(s.Id));
                _db.PushSubscriptions.RemoveRange(toRemove);
                await _db.SaveChangesAsync();
            }
        }
    }
}
