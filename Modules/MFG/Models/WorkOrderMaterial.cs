using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 工單工序用料 — RunMPS 由 RoutingBom 計算需求量展開
    /// （需求量 = QT_WO × QT_M / QT_LOT × (1 + ScrapRate/100)，mfg-domain.md §5.1）。
    /// 含 UomCode（發料單位，與庫存單位可能不同）。
    /// </summary>
    public class WorkOrderMaterial
    {
        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        public int BomSeq { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>計算後的需求發料量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal RequiredQty { get; set; }

        /// <summary>發料計量單位（必填，與庫存單位可能不同）</summary>
        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        /// <summary>遞迴下階自製工單連結（CD_MRP=M 時）</summary>
        [MaxLength(40)]
        public string? ChildWorkOrderId { get; set; }

        public MrpMode SourceMode { get; set; } = MrpMode.Make;

        /// <summary>
        /// 工單建立時快照自 Product.StandardCost（之後不隨標準成本異動，mfg-domain.md §16.2）；
        /// 釋出時用於計算 WorkOrder.StandardMaterialCost。
        /// </summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitCost { get; set; }

        // ── Phase F：發料三模式（mfg-domain.md §17.3）────────────────────────────

        /// <summary>快照自 RoutingBom → PlantSetting Fallback 鏈（mfg-domain.md §3.1）。</summary>
        public MaterialIssueMethod IssueMethod { get; set; } = MaterialIssueMethod.Push;

        /// <summary>快照自 RoutingBom.BackflushTiming（Backflush 模式時生效）。</summary>
        public BackflushTiming BackflushTiming { get; set; } = BackflushTiming.AtCompletion;

        /// <summary>累計已發出量（Push/Pull 逐批累加；Backflush 完工時一次寫入）。</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal IssuedQty { get; set; } = 0M;

        /// <summary>累計退料量（退料時累加）。</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ReturnedQty { get; set; } = 0M;

        public WorkOrderOperation? Operation { get; set; }
    }
}
