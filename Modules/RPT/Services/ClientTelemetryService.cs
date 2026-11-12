using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>手機送來的一批遙測（見 wwwroot/mobile/js/telemetry.js）。</summary>
    public sealed record ClientTelemetryBatch(string? DeviceId, string? AppVersion, List<ClientTelemetryEventDto>? Events);

    public sealed record ClientTelemetryEventDto(
        string? Kind, DateTime? At, string? Page, string? Message, string? Detail, string? Endpoint,
        int? Count, long? DurationMs, int? MaxMs, int? Under1s, int? Under3s, int? Under10s, int? Over10s,
        int? Failures, int? QueuePending, int? QueueOldestMinutes, string? NetType, int? RttMs, int? Conflicts = null);

    /// <summary>回報者是誰（由控制器從登入狀態帶入；未登入時三者皆 null）。</summary>
    public sealed record ClientTelemetrySource(string? UserId, string? UserName, string? PlantId, string? Ip, string? UserAgent);

    public interface IClientTelemetryService
    {
        /// <summary>清洗並寫入；回傳實際寫入筆數。被限流時回 -1。</summary>
        Task<int> IngestAsync(ClientTelemetryBatch batch, ClientTelemetrySource source, CancellationToken ct = default);
    }

    /// <summary>
    /// 遙測的清洗、限流與寫入。
    ///
    /// <para><b>上報端點允許未登入</b>（登入頁本身出錯也要看得到），所以這裡就是唯一的防線：
    /// 未登入只收錯誤兩種、每批最多 5 筆；登入後每批最多 50 筆；未登入每個來源 IP、登入後每個使用者＋裝置，每分鐘最多 30 批（登入者另有每人 300 批總量）。
    /// 字串一律截斷、數字夾在合理範圍、路徑去掉查詢字串並把編號片段換成 {id}。</para>
    ///
    /// <para><b>限流不用全站的 RateLimiter</b>：Program 的 <c>OnRejected</c> 不分政策，任何一次拒絕都會發
    /// 「登入暴力破解」安全告警——掛上去的話現場網路一抖就誤報資安事件。</para>
    /// </summary>
    public sealed class ClientTelemetryService : IClientTelemetryService
    {
        public const int MaxEventsAuthenticated = 50;
        public const int MaxEventsAnonymous = 5;
        public const int MaxBatchesPerMinutePerIp = 30;

        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ClientTelemetryService> _logger;

        public ClientTelemetryService(ApplicationDbContext db, IMemoryCache cache, ILogger<ClientTelemetryService> logger)
        {
            _db = db;
            _cache = cache;
            _logger = logger;
        }

        public async Task<int> IngestAsync(ClientTelemetryBatch batch, ClientTelemetrySource source, CancellationToken ct = default)
        {
            if (!TryAcquire(_cache, batch, source, DateTime.UtcNow)) return -1;

            var rows = Sanitize(batch, source, DateTime.UtcNow);
            if (rows.Count == 0) return 0;
            try
            {
                await InsertAsync(rows, ct);
            }
            catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or InvalidOperationException)
            {
                // 遙測寫不進去不能變成使用者看得到的錯誤（授權唯讀、資料庫忙）；記一行就好
                _logger.LogWarning(ex, "現場遙測寫入失敗，丟棄 {Count} 筆", rows.Count);
                return 0;
            }
            return rows.Count;
        }

        /// <summary>登入者每人每分鐘的總批數上限（跨所有裝置），擋住拿亂數裝置代號洗限額的人。</summary>
        public const int MaxBatchesPerMinutePerUser = 300;

        /// <summary>
        /// 限流單位：**未登入**以來源 IP 計（每分鐘 <see cref="MaxBatchesPerMinutePerIp"/> 批）；
        /// **已登入**以「使用者＋裝置」計（同上限），另有每人 <see cref="MaxBatchesPerMinutePerUser"/> 的總量上限。
        ///
        /// <para>原本不分登入一律以 IP 計：整個廠區經 NAT 出來只是一個 IP，而手機每換一頁就用 sendBeacon 送一批，
        /// 十支手機一起作業就超過 30 批／分，之後的遙測被 429 擋掉——sendBeacon 不看回應、送出就清掉緩衝，
        /// 等於**現場越忙越看不到資料**，沒有任何訊號。2026-11-09 nightly 的 MobileTelemetryE2ETests
        /// 因前面二十支手機測試共用 127.0.0.1 撞到同一個上限而紅，才查到這裡。</para>
        /// </summary>
        internal static bool TryAcquire(IMemoryCache cache, ClientTelemetryBatch batch, ClientTelemetrySource source, DateTime nowUtc)
        {
            var minute = nowUtc.ToString("yyyyMMddHHmm");
            if (string.IsNullOrEmpty(source.UserId))
                return Hit(cache, $"telemetry-rate:ip:{source.Ip ?? "unknown"}:{minute}") <= MaxBatchesPerMinutePerIp;

            var device = string.IsNullOrEmpty(batch.DeviceId) ? "-" : batch.DeviceId.Length > 64 ? batch.DeviceId[..64] : batch.DeviceId;
            if (Hit(cache, $"telemetry-rate:user:{source.UserId}:{minute}") > MaxBatchesPerMinutePerUser) return false;
            return Hit(cache, $"telemetry-rate:dev:{source.UserId}:{device}:{minute}") <= MaxBatchesPerMinutePerIp;
        }

        private static int Hit(IMemoryCache cache, string key)
        {
            var counter = cache.GetOrCreate(key, e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                return new StrongBox();
            })!;
            return Interlocked.Increment(ref counter.Value);
        }

        private sealed class StrongBox { public int Value; }

        internal static List<ClientTelemetryEvent> Sanitize(ClientTelemetryBatch batch, ClientTelemetrySource source, DateTime nowUtc)
        {
            var anonymous = string.IsNullOrEmpty(source.UserId);
            var max = anonymous ? MaxEventsAnonymous : MaxEventsAuthenticated;
            var allowed = anonymous ? ClientTelemetryKind.Anonymous : ClientTelemetryKind.All;
            var deviceId = Clip(batch.DeviceId, 40);
            var appVersion = Clip(batch.AppVersion, 40);
            var userAgent = Clip(source.UserAgent, 300);

            var result = new List<ClientTelemetryEvent>();
            foreach (var e in batch.Events ?? new())
            {
                if (result.Count >= max) break;
                if (e?.Kind == null || !allowed.Contains(e.Kind)) continue;

                result.Add(new ClientTelemetryEvent
                {
                    ReceivedAt = nowUtc,
                    // 手機時鐘只供參考：離收到時間太遠就不採信（假時鐘、時區錯）
                    OccurredAt = e.At is { } at && Math.Abs((at.ToUniversalTime() - nowUtc).TotalDays) <= 7
                        ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : null,
                    PlantId = Clip(source.PlantId, 10),
                    UserId = Clip(source.UserId, 450),
                    UserName = Clip(source.UserName, 256),
                    DeviceId = deviceId,
                    Kind = e.Kind,
                    Page = Clip(StripQuery(e.Page), 200),
                    Message = Clip(e.Message, 500),
                    Detail = Clip(e.Detail, 2000),
                    Endpoint = Clip(NormalizeEndpoint(e.Endpoint), 200),
                    Count = Clamp(e.Count, 1, 100_000) ?? 1,
                    DurationMs = e.DurationMs is { } d ? Math.Clamp(d, 0, 86_400_000L * 7) : null,
                    MaxMs = Clamp(e.MaxMs, 0, 86_400_000),
                    Under1s = Clamp(e.Under1s, 0, 100_000),
                    Under3s = Clamp(e.Under3s, 0, 100_000),
                    Under10s = Clamp(e.Under10s, 0, 100_000),
                    Over10s = Clamp(e.Over10s, 0, 100_000),
                    Failures = Clamp(e.Failures, 0, 100_000),
                    Conflicts = Clamp(e.Conflicts, 0, 100_000),
                    QueuePending = Clamp(e.QueuePending, 0, 100_000),
                    QueueOldestMinutes = Clamp(e.QueueOldestMinutes, 0, 60 * 24 * 365),
                    NetType = Clip(e.NetType, 10),
                    RttMs = Clamp(e.RttMs, 0, 600_000),
                    AppVersion = appVersion,
                    UserAgent = userAgent,
                });
            }
            return result;
        }

        private static readonly Regex IdLikeSegment = new(@"\d|^[0-9a-fA-F-]{20,}$", RegexOptions.Compiled);

        /// <summary>
        /// API 路徑去掉查詢字串、把帶數字或很長的片段換成 {id}（/api/mobile/sessions/{guid}/complete、
        /// /api/mobile/machines/M01/status-event）——不換的話每張派工都是不同的一列，彙總不起來。
        /// 手機端已做過一次，這裡再做一次：不能相信送進來的資料。
        /// </summary>
        internal static string? NormalizeEndpoint(string? endpoint)
        {
            var path = StripQuery(endpoint);
            if (string.IsNullOrEmpty(path)) return path;
            var segs = path.Split('/');
            for (var i = 0; i < segs.Length; i++)
                if (segs[i].Length > 24 || IdLikeSegment.IsMatch(segs[i])) segs[i] = "{id}";
            return string.Join('/', segs);
        }

        private static string? StripQuery(string? path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var cut = path.IndexOfAny(new[] { '?', '#' });
            path = cut >= 0 ? path[..cut] : path;
            // 只留路徑：送來完整網址時去掉協定與主機
            if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
                path = abs.AbsolutePath;
            return path;
        }

        private static string? Clip(string? s, int max) =>
            string.IsNullOrEmpty(s) ? null : (s.Length <= max ? s : s[..max]);

        private static int? Clamp(int? v, int min, int max) => v is { } x ? Math.Clamp(x, min, max) : null;

        /// <summary>
        /// 參數化的多列 INSERT。不經 SaveChanges：那條路上的稽核攔截器會替每一筆遙測寫一筆 AuditLog。
        /// 仍走 EF 的命令管線，授權唯讀等命令攔截器照樣生效。
        /// </summary>
        private async Task InsertAsync(List<ClientTelemetryEvent> rows, CancellationToken ct)
        {
            var cols = new[]
            {
                "ReceivedAt", "OccurredAt", "PlantId", "UserId", "UserName", "DeviceId", "Kind", "Page", "Message", "Detail",
                "Endpoint", "Count", "DurationMs", "MaxMs", "Under1s", "Under3s", "Under10s", "Over10s", "Failures",
                "QueuePending", "QueueOldestMinutes", "NetType", "RttMs", "AppVersion", "UserAgent", "Conflicts",
            };
            // 型別逐欄指定：多列 VALUES 裡的 NULL 參數不指定型別時，PostgreSQL 依整欄推成 text，
            // 寫進 integer／timestamptz 欄位就失敗（只在那一欄整批都是 NULL 時才發生，最難重現）
            var types = new[]
            {
                NpgsqlDbType.TimestampTz, NpgsqlDbType.TimestampTz, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar,
                NpgsqlDbType.Varchar, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar,
                NpgsqlDbType.Varchar, NpgsqlDbType.Integer, NpgsqlDbType.Bigint, NpgsqlDbType.Integer, NpgsqlDbType.Integer,
                NpgsqlDbType.Integer, NpgsqlDbType.Integer, NpgsqlDbType.Integer, NpgsqlDbType.Integer, NpgsqlDbType.Integer,
                NpgsqlDbType.Integer, NpgsqlDbType.Varchar, NpgsqlDbType.Integer, NpgsqlDbType.Varchar, NpgsqlDbType.Varchar,
                NpgsqlDbType.Integer,
            };
            var sql = new StringBuilder("INSERT INTO \"ClientTelemetryEvents\" (")
                .Append(string.Join(',', cols.Select(c => $"\"{c}\""))).Append(") VALUES ");
            var ps = new List<NpgsqlParameter>();
            for (var r = 0; r < rows.Count; r++)
            {
                var e = rows[r];
                object?[] vals =
                {
                    e.ReceivedAt, e.OccurredAt, e.PlantId, e.UserId, e.UserName, e.DeviceId, e.Kind, e.Page, e.Message, e.Detail,
                    e.Endpoint, e.Count, e.DurationMs, e.MaxMs, e.Under1s, e.Under3s, e.Under10s, e.Over10s, e.Failures,
                    e.QueuePending, e.QueueOldestMinutes, e.NetType, e.RttMs, e.AppVersion, e.UserAgent, e.Conflicts,
                };
                if (r > 0) sql.Append(',');
                sql.Append('(');
                for (var c = 0; c < vals.Length; c++)
                {
                    var name = $"p{r}_{c}";
                    if (c > 0) sql.Append(',');
                    sql.Append('@').Append(name);
                    ps.Add(new NpgsqlParameter(name, types[c]) { Value = vals[c] ?? DBNull.Value });
                }
                sql.Append(')');
            }
            await _db.Database.ExecuteSqlRawAsync(sql.ToString(), ps, ct);
        }
    }
}
