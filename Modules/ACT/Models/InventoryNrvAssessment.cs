using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    /// <summary>IAS 2 存貨淨變現價值評估（Phase C，§4.5）。
    /// 月末批次產生，比較 AVCO 帳面成本與 NRV，超出者計提減損損失。</summary>
    public class InventoryNrvAssessment : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>AVCO 帳面單位成本</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal CarryingCost { get; set; }

        /// <summary>在庫數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyOnHand { get; set; }

        /// <summary>預期銷售單價（近 90 天平均 AR Invoice 單價）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal NrvPrice { get; set; }

        /// <summary>估計銷售費用（可設定為 NrvPrice 的固定比率）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal SellingCost { get; set; }

        /// <summary>NRV = NrvPrice - SellingCost</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal NetRealizableValue { get; set; }

        /// <summary>max(0, (CarryingCost - NRV) × QtyOnHand)</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ImpairmentLoss { get; set; }

        public DateTime AssessedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }
}
