using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    public enum CostStatus
    {
        Preliminary = 0,  // 初步（完工當下計算）
        Final       = 1   // 最終（月末費率重評後）
    }

    /// <summary>工單實際成本結算（Phase B，mfg-domain.md §3.3）。
    /// 完工時由 ProductionCompletionService 自動建立，含三項差異科目。</summary>
    public class WorkOrderActualCost : IPlantEntity
    {
        [Key, MaxLength(30)]
        public string WorkOrderId { get; set; } = string.Empty;

        // ── 實際成本 ─────────────────────────────────────────────
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualMaterial { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualLabor { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualOverhead { get; set; }

        /// <summary>實際委外成本（工序委外的加工費＋帶出料，mfg-domain.md §19.7 C）；無標準值、不列差異。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualOutsource { get; set; }

        /// <summary>
        /// 重工損失（只有重工工單有值，mfg-domain.md §16.4b）：實際總成本超出「不良品原值」、沒有回到存貨而轉入
        /// 5250 重工損失的金額。完工入庫時計算；之後期末費率重估的差額照一般工單進 5130，不回頭改這裡。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReworkLoss { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualTotal => ActualMaterial + ActualLabor + ActualOverhead + ActualOutsource;

        // ── 標準成本快照（釋出時鎖定）────────────────────────────
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardMaterial { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardLabor { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardOverhead { get; set; }

        // ── 差異（Actual - Standard，正數＝超支）────────────────────
        [Column(TypeName = "decimal(18,4)")]
        public decimal VarMaterial { get; set; }          // 料差

        [Column(TypeName = "decimal(18,4)")]
        public decimal VarLaborEfficiency { get; set; }   // 人工效率差

        [Column(TypeName = "decimal(18,4)")]
        public decimal VarOverheadRate { get; set; }      // 製費費率差

        // ── 中繼資料 ─────────────────────────────────────────────
        public DateOnly CostDate { get; set; }
        public CostStatus Status { get; set; } = CostStatus.Preliminary;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }
}
