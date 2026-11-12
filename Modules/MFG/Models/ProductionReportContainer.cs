using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 報工容器 — 記錄每筆報工使用的容器與實際入庫量（mfg-domain.md §3 Entity 表）。
    /// CD_BOR 定量轉移以容器為單位時填入；一筆報工可對應多個容器。
    /// </summary>
    public class ProductionReportContainer
    {
        [Required, MaxLength(40)]
        public string ReportId { get; set; } = string.Empty;

        public int ContainerSeq { get; set; }

        [Required, MaxLength(30)]
        public string ContainerCode { get; set; } = string.Empty;

        /// <summary>實際入庫量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ActualQty { get; set; }

        /// <summary>裝進哪一個箱子（箱子個體，§9.4）；只選容器種類時為 null</summary>
        [MaxLength(30)]
        public string? ContainerUnitId { get; set; }

        public ProductionReport? Report { get; set; }
    }
}
