using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 庫存異動 Log（不可變：只寫入，絕不修改或刪除。作廢以反向沖銷處理）。
    /// 取代舊版 InventoryMovement。
    /// </summary>
    public class StockMovement : IPlantEntity
    {
        [Key]
        public Guid MovementId { get; set; } = Guid.NewGuid();

        /// <summary>異動類別代號 → FK to InventoryTransactionType</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "";

        /// <summary>異動類別分類（冗餘，提升查詢效率）</summary>
        [MaxLength(20)]
        public string? TypeCategory { get; set; }

        /// <summary>單據編號</summary>
        [Required, MaxLength(30)]
        public string DocNo { get; set; } = "";

        /// <summary>單據行號</summary>
        public int DocLineNo { get; set; }

        /// <summary>來源單號（如採購單號）</summary>
        [MaxLength(30)]
        public string? SourceDocNo { get; set; }

        /// <summary>來源行號</summary>
        public int? SourceDocLineNo { get; set; }

        /// <summary>異動日期（廠別本地業務日期）</summary>
        public DateOnly MovementDate { get; set; }

        [Required, MaxLength(20)]
        public string PlantId { get; set; } = "";

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = "";

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = "";

        /// <summary>批號（空字串 = 不追蹤）</summary>
        [MaxLength(50)]
        public string LotNo { get; set; } = "";

        /// <summary>業務單位數量</summary>
        [MaxLength(20)]
        public string UomCode { get; set; } = "";

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        /// <summary>庫存單位</summary>
        [MaxLength(20)]
        public string StockUomCode { get; set; } = "";

        /// <summary>庫存單位數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        /// <summary>方向 +1=入庫 -1=出庫</summary>
        public int Direction { get; set; }

        /// <summary>異動當下單位成本</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? UnitCost { get; set; }

        /// <summary>= UnitCost × StockQty</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? TotalCost { get; set; }

        [MaxLength(450)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(500)]
        public string? Remark { get; set; }

        // IPlantEntity
        string? IPlantEntity.PlantId { get => PlantId; set => PlantId = value ?? ""; }
    }
}
