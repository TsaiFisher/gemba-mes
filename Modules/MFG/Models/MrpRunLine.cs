using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>產銷模式 CD_MRP（mfg-domain.md §6）</summary>
    public enum MrpMode
    {
        StockOnly = 0,  // Q 庫存出貨
        Purchase = 1,   // P 採購
        Outsource = 2,  // S 委外
        Make = 3        // M 自製
    }

    /// <summary>
    /// MRP 執行明細 — 純 MRP 計算結果紀錄（QT_Q / QT_PO / QT_OO / QT_WO），
    /// 批次追蹤責任已移交 ProductionBatch（mfg-domain.md §14.3）。
    /// </summary>
    public class MrpRunLine
    {
        [Required, MaxLength(30)]
        public string MrpRunId { get; set; } = string.Empty;

        public int LineSeq { get; set; }

        /// <summary>來源訂單號（NO_SO）</summary>
        [MaxLength(30)]
        public string? SalesOrderId { get; set; }

        public int? SalesOrderLineSeq { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>訂單需求量 QT_SO</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal RequiredQty { get; set; }

        /// <summary>交期 DT_DLV</summary>
        public DateTime RequiredDate { get; set; }

        public MrpMode Mode { get; set; } = MrpMode.Make;

        /// <summary>批次優先序（展開時帶入 ProductionBatch.Priority；小者先排，預設 100）。</summary>
        public int Priority { get; set; } = 100;

        /// <summary>可用庫存滿足量 QT_Q</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal StockQty { get; set; }

        /// <summary>採購量 QT_PO</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal PurchaseQty { get; set; }

        /// <summary>委外量 QT_OO</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal OutsourceQty { get; set; }

        /// <summary>自製量 QT_WO</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal MakeQty { get; set; }

        /// <summary>展開產生的生產批號（= ProductionBatch.BatchId）</summary>
        [MaxLength(30)]
        public string? ProductionBatchId { get; set; }

        /// <summary>
        /// 可行性預檢錯誤（mfg-domain.md §11 Phase B 落地）：展開前驗證該列途程/BOM/單位換算，
        /// 有阻擋性錯誤即回填此欄並跳過該列展開（其餘列照展）；修正主檔後重建需求列重跑。空＝通過。
        /// </summary>
        [MaxLength(500)]
        public string? ValidationError { get; set; }

        public MrpRun? MrpRun { get; set; }
    }
}
