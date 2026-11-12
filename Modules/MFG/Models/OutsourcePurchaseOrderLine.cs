using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 委外採購單明細（收回品）：對應一筆待轉委外的委外建議（OutsourceDemand）。
    /// 支援拆單——同一 DemandId 可建立多行（不同廠商/交期/數量），SplitQty 為本行拆單量。
    /// </summary>
    public class OutsourcePurchaseOrderLine
    {
        [MaxLength(30)]
        public string OutsourcePurchaseOrderId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>來源委外建議單（FK → OutsourceDemand）</summary>
        [MaxLength(40)]
        public string? DemandId { get; set; }

        /// <summary>來源工單（冗餘，方便查詢）</summary>
        [MaxLength(40)]
        public string SourceWorkOrderId { get; set; } = string.Empty;

        /// <summary>來源委外派工的工序序（整品委外為其單一委外工序序）</summary>
        public int OperationSeq { get; set; }

        /// <summary>收回品（= 來源工單品號）</summary>
        [MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>委外加工數量（= 來源派工數量）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Qty { get; set; }

        /// <summary>本次拆單數量（≤ DemandQty - 已轉量；拆單時由 UI 指定）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal SplitQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        /// <summary>委外加工單價（從 OutsourceQuotation 自動帶入，可手動覆蓋）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 合約允收損耗率的快照（轉單當下自 <c>OutsourceQuotation</c> 帶入）。
        /// <c>null</c> ＝ 合約未約定損耗條款 → **不扣款**；<c>0</c> ＝ 明確約定零容忍。
        /// 與 <c>UnitPrice</c> 同一條快照鏈：報價 → 委外採購單 → 委外收貨明細；
        /// 快照而不是每次回查報價——報價會改、會過期，已下出去的單不該跟著變。
        /// </summary>
        [Range(0, 1)]
        [Column(TypeName = "decimal(8,6)")]
        public decimal? AllowedScrapRate { get; set; }

        /// <summary>委外報價幣別 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        public OutsourcePurchaseOrder? OutsourcePurchaseOrder { get; set; }
        public OutsourceDemand? Demand { get; set; }
    }
}
