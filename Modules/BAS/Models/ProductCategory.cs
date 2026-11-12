using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>產品類別主檔（PC）— 含庫存管理預設設定</summary>
    public class ProductCategory
    {
        [Key, MaxLength(20)]
        public string CategoryCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string CategoryName { get; set; } = string.Empty;

        /// <summary>預設是否啟用批號管理</summary>
        public bool EnableLotTracking { get; set; } = false;

        /// <summary>預設倉庫 → FK to Warehouse</summary>
        [MaxLength(20)]
        public string? DefaultWarehouseCode { get; set; }

        /// <summary>預設儲位 → FK to StorageLocation.LocationCode</summary>
        [MaxLength(20)]
        public string? DefaultLocationCode { get; set; }

        /// <summary>預設庫存單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? DefaultStockUomCode { get; set; }

        /// <summary>成本計算方式</summary>
        public CostMethod CostMethod { get; set; } = CostMethod.SystemDefault;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    }

    public enum CostMethod
    {
        SystemDefault,  // 遵照系統全域設定
        MovingAverage,  // 移動加權平均
        Fifo            // 先進先出
    }
}
