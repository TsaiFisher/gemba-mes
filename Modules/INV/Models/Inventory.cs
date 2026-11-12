using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 庫存餘額快照。以 PlantId + ProductCode + LocationCode + LotNo 為唯一鍵。
    /// 併發控制採用 PostgreSQL xmin（由 UseXminAsConcurrencyToken 設定）。
    /// </summary>
    public class Inventory : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string PlantId { get; set; } = "";

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = "";

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = "";

        /// <summary>批號（空字串 = 不追蹤批號）</summary>
        [Required, MaxLength(50)]
        public string LotNo { get; set; } = "";

        /// <summary>庫存量（庫存單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyOnHand { get; set; }

        /// <summary>預留量（庫存單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyReserved { get; set; }

        // QtyAvailable = QtyOnHand - QtyReserved（計算屬性，不存 DB）
        [NotMapped]
        public decimal QtyAvailable => QtyOnHand - QtyReserved;

        /// <summary>移動加權平均單位成本</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitCost { get; set; }

        /// <summary>批號到期日（null = 無有效期限）</summary>
        public DateOnly? LotExpiryDate { get; set; }

        public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity（Global Filter 用，值 = PlantId）
        string? IPlantEntity.PlantId { get => PlantId; set => PlantId = value ?? ""; }
    }
}
