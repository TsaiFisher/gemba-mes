using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 手機 API 客戶端去重日誌（mfg-domain.md §12.2 / mobile-pwa.md §8.3）。
    /// 同一 clientId 第二次 POST 回傳第一次結果，DB 不重複寫入（幂等性）。
    /// </summary>
    public class SyncLog
    {
        [Key, MaxLength(80)]
        public string ClientId { get; set; } = string.Empty;

        /// <summary>最後操作端點（如 "/api/mobile/sessions"）</summary>
        [MaxLength(100)]
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>客戶端操作時間戳記</summary>
        public DateTime ClientTimestamp { get; set; }

        /// <summary>後端建立時間</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>後端回應內容快照（JSON），用於重放結果</summary>
        [MaxLength(2000)]
        public string? ResponseSnapshot { get; set; }
    }
}
