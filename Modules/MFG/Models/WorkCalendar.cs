using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>工作日曆 — IPlantEntity，廠別/工作中心/機台三層設定</summary>
    public class WorkCalendar : IPlantEntity
    {
        public int Id { get; set; }

        public string? PlantId { get; set; }

        /// <summary>班制代號（展開來源，對應 ShiftGroup.GroupCode）</summary>
        [MaxLength(20)]
        public string? ShiftGroupCode { get; set; }

        [MaxLength(30)]
        public string? WorkCenterId { get; set; }

        [MaxLength(20)]
        public string? MachineCode { get; set; }

        public DateOnly Date { get; set; }

        /// <summary>正班工作開始時間（null = 全天 24h）</summary>
        public TimeOnly? WorkStart { get; set; }

        /// <summary>正班工作結束時間（WorkEnd &lt; WorkStart 表示跨午夜）</summary>
        public TimeOnly? WorkEnd { get; set; }

        [Column(TypeName = "decimal(6,2)")]
        public decimal TM_WKS { get; set; } = 0;

        [Column(TypeName = "decimal(6,2)")]
        public decimal TM_WKO { get; set; } = 0;

        [MaxLength(200)]
        public string? Note { get; set; }
    }
}
