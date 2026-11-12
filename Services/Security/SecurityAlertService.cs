using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SmartFactory.MES.Services.Security
{
    /// <summary>
    /// M6：<see cref="ISecurityAlertService"/> 的預設實作（Singleton）。
    /// 無外部相依即可運作——僅寫結構化高嚴重度日誌；設定 webhook 後另發 HTTP 通知。
    /// </summary>
    public sealed class SecurityAlertService : ISecurityAlertService
    {
        private readonly ILogger<SecurityAlertService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        // (kind:ip) → 上次告警時間（UTC）。冷卻窗內重複事件僅去重不重送。
        private readonly ConcurrentDictionary<string, DateTime> _lastRaised = new();

        public SecurityAlertService(
            ILogger<SecurityAlertService> logger,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>冷卻窗（分鐘）。可由 <c>Security:AlertCooldownMinutes</c> 覆寫；預設 5 分鐘。</summary>
        private int CooldownMinutes()
        {
            var v = _configuration.GetValue<int?>("Security:AlertCooldownMinutes") ?? 5;
            return v > 0 ? v : 5;
        }

        private string? WebhookUrl() => _configuration["Security:AlertWebhookUrl"];

        /// <summary>
        /// 判斷此 (kind:ip) 是否應在 <paramref name="now"/> 送出（冷卻窗外或初次）。
        /// 純函式化便於單元測試；會更新去重時戳。
        /// </summary>
        public bool ShouldRaise(string kind, string? ipAddress, DateTime now)
        {
            var key = $"{kind}:{ipAddress ?? "unknown"}";
            var window = TimeSpan.FromMinutes(CooldownMinutes());
            var send = false;
            _lastRaised.AddOrUpdate(key,
                _ => { send = true; return now; },
                (_, last) =>
                {
                    if (now - last >= window) { send = true; return now; }
                    return last;
                });
            return send;
        }

        public async Task RaiseAsync(string kind, string detail, string? ipAddress)
        {
            var now = DateTime.UtcNow;
            if (!ShouldRaise(kind, ipAddress, now))
            {
                return; // 冷卻窗內，去重
            }

            var ip = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress;

            // 結構化高嚴重度日誌——SECURITY_ALERT 標記供 log shipping / SIEM 過濾。
            _logger.LogWarning(
                "SECURITY_ALERT kind={AlertKind} ip={ClientIp} detail={AlertDetail} at={TimestampUtc:o}",
                kind, ip, detail, now);

            var url = WebhookUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                return; // 未設 webhook：僅日誌即為告警管道。
            }

            // fire-and-forget：webhook 不可阻塞或拋出到請求管線（告警失敗不得影響服務）。
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    source = "SmartFactory.MES",
                    kind,
                    ip,
                    detail,
                    at = now.ToString("o"),
                });
                using var client = _httpClientFactory.CreateClient("security-alert");
                client.Timeout = TimeSpan.FromSeconds(5);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await client.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                // 告警管道自身故障只記 Debug，不上升為錯誤，避免告警風暴放大成服務噪音。
                _logger.LogDebug(ex, "SECURITY_ALERT webhook 發送失敗 kind={AlertKind}", kind);
            }
        }
    }
}
