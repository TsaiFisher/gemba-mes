using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// FIFO 成本層。僅在 ProductCategory.CostMethod == Fifo 時使用。
    /// 入庫由 InventoryCostLayerService.AddLayerAsync 建層、出庫由 ConsumeLayersAsync 依 ReceiptDate 先進先出消耗；
    /// 由 InventoryPostingService 依 CostMethod 分支呼叫。
    /// </summary>
    public class InventoryCostLayer : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string PlantId { get; set; } = "";

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = "";

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = "";

        [MaxLength(50)]
        public string LotNo { get; set; } = "";

        /// <summary>入庫日（FIFO 排序依據）</summary>
        public DateOnly ReceiptDate { get; set; }

        /// <summary>來源單號</summary>
        [Required, MaxLength(30)]
        public string SourceDocNo { get; set; } = "";

        /// <summary>此層剩餘量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyRemaining { get; set; }

        /// <summary>此層單位成本</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitCost { get; set; }

        // IPlantEntity
        string? IPlantEntity.PlantId { get => PlantId; set => PlantId = value ?? ""; }
    }
}
