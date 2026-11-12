using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>在製品（WIP）期末評估快照（roadmap-fin-cst-sub.md §3.5）。
    /// 月結時對進行中工單以「實際費率」重估已報工人工 / 製費，連同已領料成本記錄期末 WIP 價值。
    /// 每期 × 每工單一筆，重跑時刪舊重建（冪等，比照 FxRevaluation）。</summary>
    public class WipSnapshot : IPlantEntity
    {
        public int Id { get; set; }

        /// <summary>會計期間 YYYYMM</summary>
        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string WorkOrderId { get; set; } = string.Empty;

        /// <summary>已領料成本（WIP 材料）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal WipMaterial { get; set; }

        /// <summary>已報工人工（以實際費率重估）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal WipLabor { get; set; }

        /// <summary>已報工製費（以實際費率重估）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal WipOverhead { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal WipTotal => WipMaterial + WipLabor + WipOverhead;

        /// <summary>已完工數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyCompleted { get; set; }

        /// <summary>在製數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyInProcess { get; set; }

        public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }
}
