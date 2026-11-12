using System.Globalization;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機斷線補送的「發生時間」換算（mobile-pwa.md §5.2 斷線保護）。
    ///
    /// <para>🚨 原本伺服器只拿 clientTimestamp 去重，下工／報工／機台狀態一律記成**送達當下**：
    /// 10:00 斷線按下工、10:25 才送到，工時就多算 25 分鐘，人工／機台成本、overhead、稼動率跟著偏高。</para>
    ///
    /// <para>不能直接採用 clientTimestamp：手機時鐘可能快慢好幾分鐘（甚至被改過）。
    /// 佇列送出時另帶 <see cref="SentAtHeader"/>（同一支手機的時鐘），兩者相減是
    /// 「排隊了多久」，時鐘偏差在相減時抵銷；發生時間＝伺服器現在 − 排隊時間。</para>
    ///
    /// <list type="bullet">
    /// <item>沒帶 header（直接連線送出，或舊版前端）→ 排隊時間視為 0，與原本行為相同。</item>
    /// <item>排隊時間為負（手機中途改過時鐘）→ 視為 0，絕不產生未來時間。</item>
    /// <item>超過 <see cref="MaxBackdate"/> → 呼叫端拒收：斷線保護只負責短暫斷線，
    ///       隔天才送到的資料要改在桌機補登，不能默默記成一個猜的時間。</item>
    /// </list>
    /// </summary>
    public static class MobileOccurredAt
    {
        /// <summary>佇列送出時帶的 header：手機當下時間（ISO 8601）。</summary>
        public const string SentAtHeader = "X-Client-Sent-At";

        /// <summary>補送可回推的上限。手機端超過 30 分鐘就改為人工確認後才送，這裡是伺服器的最後防線。</summary>
        public static readonly TimeSpan MaxBackdate = TimeSpan.FromHours(24);

        /// <summary>
        /// 換算發生時間（UTC）。回傳 null＝超過 <see cref="MaxBackdate"/>，<paramref name="queuedFor"/> 仍會給出排隊時間供訊息使用。
        /// </summary>
        public static DateTime? Resolve(DateTime clientTimestamp, string? sentAtHeader, DateTime serverNowUtc, out TimeSpan queuedFor)
        {
            queuedFor = TimeSpan.Zero;
            if (!string.IsNullOrWhiteSpace(sentAtHeader)
                && DateTimeOffset.TryParse(sentAtHeader, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal, out var sentAt))
            {
                var elapsed = sentAt.UtcDateTime - ToUtc(clientTimestamp);
                if (elapsed > TimeSpan.Zero) queuedFor = elapsed;
            }
            if (queuedFor > MaxBackdate) return null;
            return serverNowUtc - queuedFor;
        }

        /// <summary>JSON 帶 Z 的時間會解成 Utc；沒帶時區的視為 UTC（前端一律送 toISOString）。</summary>
        private static DateTime ToUtc(DateTime t) => t.Kind switch
        {
            DateTimeKind.Utc   => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };
    }
}
