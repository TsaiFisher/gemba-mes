using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 計劃停機（mfg-domain.md §產能模型）— 機台/工作中心/全廠的「未來不可用時間區段」（保養、改裝、教育訓練等）。
    /// 與機台狀態 A–H（點狀「當下可否排程」）互補：本表表達「未來時間區段」。
    /// 消費：Phase C 有限產能排程於可用時段中扣除與本停機重疊的部分（執行期相減，不物化進 WorkCalendarSegment）。
    /// </summary>
    public class PlannedDowntime : IPlantEntity
    {
        public int Id { get; set; }

        public string? PlantId { get; set; }

        /// <summary>工作中心（空 = 全廠）。</summary>
        [MaxLength(30)]
        public string? WorkCenterId { get; set; }

        /// <summary>機台（空 = 整個工作中心 / 全廠）。</summary>
        [MaxLength(20)]
        public string? MachineCode { get; set; }

        /// <summary>停機起（含）。廠別本地壁鐘時間（timestamp without time zone，避免 datetime-local 的 Unspecified Kind 寫入 timestamptz 失敗）。</summary>
        [Column(TypeName = "timestamp without time zone")]
        public DateTime StartTime { get; set; }

        /// <summary>停機迄（含）。廠別本地壁鐘時間。</summary>
        [Column(TypeName = "timestamp without time zone")]
        public DateTime EndTime { get; set; }

        /// <summary>停機類型 → CodeEntry: downtime_type（保養/改裝/教育訓練…，選填）。</summary>
        [MaxLength(20)]
        public string? DowntimeType { get; set; }

        [MaxLength(200)]
        public string? Reason { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
