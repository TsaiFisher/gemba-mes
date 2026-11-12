using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>工作中心（WC）</summary>
    public class WorkCenter : IPlantEntity
    {
        [Key, MaxLength(20)]
        public string WorkCenterCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string WorkCenterName { get; set; } = string.Empty;

        /// <summary>部門代號（從組織圖選取）</summary>
        [MaxLength(100)]
        public string? DepartmentId { get; set; }

        /// <summary>班制代號 → FK to ShiftGroup</summary>
        [MaxLength(20)]
        public string? ShiftGroupCode { get; set; }

        /// <summary>是否為生產線</summary>
        public bool IsProductionLine { get; set; }

        /// <summary>附加費率</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal AdditionalRate { get; set; }

        /// <summary>標準工時單價（人工費率，元/小時；對應 mfg-domain.md §16.2 LaborHourlyRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardLaborCost { get; set; }

        /// <summary>機台費率（元/小時），製造費用計算用（mfg-domain.md §16.2 MachineHourlyRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal MachineHourlyRate { get; set; }

        /// <summary>
        /// 預設備料倉：本工作中心的派工開備料單時，備料目標倉的預設值。
        /// 解析順序為「開單當下指定 &gt; 本欄位 &gt; 系統參數 mfg_default_staging_wh」，三層皆可覆寫。
        /// </summary>
        [MaxLength(20)]
        public string? StagingWhCode { get; set; }

        /// <summary>預設備料儲位（搭配 <see cref="StagingWhCode"/>；未填則由備料單開單時選擇）。</summary>
        [MaxLength(20)]
        public string? StagingLocationCode { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // Navigation
        public ICollection<WorkCenterMachine> Machines { get; set; } = new List<WorkCenterMachine>();
    }
}
