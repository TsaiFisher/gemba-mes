using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>模具 / 治具（MOLD）</summary>
    public class Tooling : IPlantEntity
    {
        [Key, MaxLength(20)]
        public string ToolingCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string ToolingName { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Spec { get; set; }

        /// <summary>穴數 / 模穴數（通用：治具亦可表示容納數量）</summary>
        public int? CavityCount { get; set; }

        /// <summary>種類 → CodeEntry: tooling_type（例：模具/治具/夾具）</summary>
        [MaxLength(20)]
        public string? ToolingType { get; set; }

        /// <summary>每穴產品組合（多品項一模時使用；mfg-domain.md §14.1）</summary>
        [MaxLength(200)]
        public string? ProductsPerCavity { get; set; }

        /// <summary>
        /// 累計打次 = Σ (QtyOk + QtyNg) / CavityCount（mfg-domain.md §14.1）。
        /// 報工回寫累加；保養完成（HO 事件、SourceDocId=ToolingCode）歸零。系統維護，非表單輸入。
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal AccumulatedShots { get; set; }

        /// <summary>
        /// 保養間隔打次（mfg-domain.md §14.1 第二階段）。報工回寫跨越此門檻時
        /// 自動建立 HI 保養停機單。null 或 0 = 不做打次保養管控（夾治具走點檢表）。
        /// </summary>
        public int? PmIntervalShots { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }
    }
}
