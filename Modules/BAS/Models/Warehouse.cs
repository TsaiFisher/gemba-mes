using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>倉庫（WH）</summary>
    public class Warehouse : IPlantEntity
    {
        [Key, MaxLength(20)]
        public string WhCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string WhName { get; set; } = string.Empty;

        /// <summary>倉庫類型（決定系統行為）</summary>
        public WarehouseType WarehouseType { get; set; } = WarehouseType.Raw;

        /// <summary>是否納入 MRP 計算</summary>
        public bool IsMrpWarehouse { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // Navigation
        public ICollection<StorageLocation> Locations { get; set; } = new List<StorageLocation>();
    }
}
