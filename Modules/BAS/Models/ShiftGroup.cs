using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>班制（SI）— 定義一週各天的班別排班</summary>
    public class ShiftGroup : IPlantEntity
    {
        [Key, MaxLength(20)]
        public string GroupCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string GroupName { get; set; } = string.Empty;

        /// <summary>是否為本廠預設班制（每廠至多一筆）。批次產生工作日曆未指定班制時，全廠層級採此預設（mfg-domain.md §3.1 工作日曆）。</summary>
        public bool IsDefault { get; set; } = false;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // Navigation
        public ICollection<ShiftGroupDay> Days { get; set; } = new List<ShiftGroupDay>();
    }
}
