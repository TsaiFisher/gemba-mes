using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 現場裝置（手機）回報的遙測事件：前端錯誤、API 耗時統計、斷線、手機上未送出的佇列。
    ///
    /// <para><b>為什麼要有</b>：現場出問題時伺服器日誌只看得到「請求到了」的那一半。
    /// 頁面腳本出錯、請求根本沒送到、訊號弱到每支 API 都要等十幾秒、手機裡卡著三天前的報工——
    /// 這些全都發生在手機上，原本沒有任何管道知道（2026-10-21）。</para>
    ///
    /// <para><b>寫入不經 EF 存檔</b>（<c>ClientTelemetryWriter</c> 用參數化 SQL）：經 SaveChanges 的每一筆
    /// 都會被稽核攔截器記一筆 AuditLog，遙測量一大稽核表就被灌爆。讀取照常走 EF。</para>
    /// </summary>
    public class ClientTelemetryEvent : IPlantEntity
    {
        [Key]
        public long Id { get; set; }

        /// <summary>伺服器收到的時間（UTC）。查詢與保留期都以它為準——手機時鐘不可信。</summary>
        public DateTime ReceivedAt { get; set; }

        /// <summary>手機回報的發生時間（UTC），只供參考。</summary>
        public DateTime? OccurredAt { get; set; }

        /// <summary>回報當下登入者所在的廠；未登入（例如登入頁出錯）為 null。</summary>
        [MaxLength(10)]
        public string? PlantId { get; set; }

        [MaxLength(450)]
        public string? UserId { get; set; }

        [MaxLength(256)]
        public string? UserName { get; set; }

        /// <summary>手機自己產生、存在瀏覽器裡的隨機代號，用來分辨「是哪一支手機」。不是硬體識別碼。</summary>
        [MaxLength(40)]
        public string? DeviceId { get; set; }

        /// <summary>事件種類，見 <see cref="ClientTelemetryKind"/>。</summary>
        [Required, MaxLength(20)]
        public string Kind { get; set; } = "";

        /// <summary>發生在哪一頁（只留路徑，不留查詢字串）。</summary>
        [MaxLength(200)]
        public string? Page { get; set; }

        [MaxLength(500)]
        public string? Message { get; set; }

        /// <summary>錯誤堆疊等細節。</summary>
        [MaxLength(2000)]
        public string? Detail { get; set; }

        /// <summary>API 路徑（編號類片段已換成 {id}，才能彙總）。</summary>
        [MaxLength(200)]
        public string? Endpoint { get; set; }

        /// <summary>同一筆在這一批裡發生的次數（錯誤去重、API 統計的呼叫數）。</summary>
        public int Count { get; set; } = 1;

        // ── API 耗時統計（Kind = ApiStats）：以分桶記，彙總時直接相加 ──

        /// <summary>耗時合計（毫秒）；斷線事件則是斷線的毫秒數。</summary>
        public long? DurationMs { get; set; }
        public int? MaxMs { get; set; }
        /// <summary>1 秒內完成的次數。</summary>
        public int? Under1s { get; set; }
        /// <summary>1～3 秒。</summary>
        public int? Under3s { get; set; }
        /// <summary>3～10 秒。</summary>
        public int? Under10s { get; set; }
        /// <summary>10 秒以上（含逾時）。</summary>
        public int? Over10s { get; set; }
        /// <summary>連不上、逾時或 5xx 的次數。</summary>
        public int? Failures { get; set; }
        /// <summary>
        /// 回 409 的次數（別人同時改了同一張單：伺服器重試用完仍衝突才會回到手機）。不算在 Failures 裡——
        /// 這筆沒存進去但會被手機佇列重送，是要看頻率的訊號，不是故障（2026-10-21 起）。
        /// </summary>
        public int? Conflicts { get; set; }

        // ── 手機上的佇列（Kind = Queue）──
        public int? QueuePending { get; set; }
        /// <summary>最舊一筆未送出的已等了幾分鐘。</summary>
        public int? QueueOldestMinutes { get; set; }

        // ── 網路狀況（navigator.connection，部分瀏覽器沒有）──
        [MaxLength(10)]
        public string? NetType { get; set; }
        public int? RttMs { get; set; }

        /// <summary>手機版號（Service Worker 快取名稱，如 mes-mobile-v36）。</summary>
        [MaxLength(40)]
        public string? AppVersion { get; set; }

        [MaxLength(300)]
        public string? UserAgent { get; set; }
    }

    /// <summary>遙測事件種類（存進 <see cref="ClientTelemetryEvent.Kind"/> 的字串）。</summary>
    public static class ClientTelemetryKind
    {
        /// <summary>頁面腳本錯誤（window.onerror）。</summary>
        public const string Error = "Error";
        /// <summary>沒有被接住的 Promise 錯誤。</summary>
        public const string Rejection = "Rejection";
        /// <summary>某支 API 在這段期間的呼叫數與耗時分桶。</summary>
        public const string ApiStats = "ApiStats";
        /// <summary>一段斷線（DurationMs＝斷了多久）。</summary>
        public const string Offline = "Offline";
        /// <summary>手機上還沒送出的佇列。</summary>
        public const string Queue = "Queue";

        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(StringComparer.Ordinal) { Error, Rejection, ApiStats, Offline, Queue };

        /// <summary>未登入時只收這兩種：登入頁出錯要看得到，但匿名端點不能讓人灌任意資料。</summary>
        public static readonly IReadOnlySet<string> Anonymous =
            new HashSet<string>(StringComparer.Ordinal) { Error, Rejection };
    }
}
