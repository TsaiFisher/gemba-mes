using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 派工並發衝突（xmin）的紀錄。用固定的 EventId，日誌裡直接以事件代號篩選統計。
    ///
    /// <para><b>為什麼要記</b>：2026-10-21 換班尖峰壓測後，手機寫入改為伺服器端重試；但「重試後成功」與
    /// 「重試仍失敗」原本都沒有任何紀錄，遙測也只算 5xx 與連不上，不算 409——上線後衝突到底多常發生看不到。
    /// 是否要再做「完成數原子遞增」或「桌機也重試」，要靠這裡的數字決定（見 deploy/capacity-and-db-connections.md）。</para>
    /// </summary>
    public static class ConcurrencyConflictLog
    {
        /// <summary>伺服器端重試後成功（手機）。</summary>
        public static readonly EventId RetrySucceeded = new(7101, "ConcurrencyRetrySucceeded");
        /// <summary>重試用完仍衝突，回 409 給手機（手機佇列會再重送）。</summary>
        public static readonly EventId RetryExhausted = new(7102, "ConcurrencyRetryExhausted");
        /// <summary>桌機遇衝突直接回 409（桌機不重試，使用者重按）。</summary>
        public static readonly EventId DesktopConflict = new(7103, "ConcurrencyConflictDesktop");

        private static ILogger? Logger(HttpContext? http) =>
            http?.RequestServices?.GetService(typeof(ILoggerFactory)) is ILoggerFactory f
                ? f.CreateLogger("SmartFactory.MES.ConcurrencyConflict") : null;

        public static void Mobile(HttpContext? http, bool succeeded, int retries)
        {
            var path = http?.Request.Path.Value;
            if (succeeded)
                Logger(http)?.LogInformation(RetrySucceeded, "派工並發衝突：伺服器重試 {Retries} 次後成功 {Path}", retries, path);
            else
                Logger(http)?.LogWarning(RetryExhausted, "派工並發衝突：伺服器重試 {Retries} 次仍衝突，回 409 {Path}", retries, path);
        }

        public static void Desktop(HttpContext? http, string action, string? dispatchId) =>
            Logger(http)?.LogWarning(DesktopConflict, "派工並發衝突（桌機 {Action}）：{DispatchId} 回 409，使用者需重按", action, dispatchId);
    }
}
