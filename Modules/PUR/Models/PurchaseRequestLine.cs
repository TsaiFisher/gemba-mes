using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>請購單明細（對應舊 PR_D 表）</summary>
    public class PurchaseRequestLine
    {
        [Required, MaxLength(30)]
        public string PurchaseRequestId { get; set; } = string.Empty;

        /// <summary>明細序號（1, 2, 3...）</summary>
        public int LineNo { get; set; }

        /// <summary>品號 → FK to Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>預交日期</summary>
        public DateOnly? DeliveryDate { get; set; }

        /// <summary>請購數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal RequestQty { get; set; }

        /// <summary>採購單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? UomCode { get; set; }

        /// <summary>庫存換算數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        /// <summary>庫存單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>已結案（Y = 不再轉採購）</summary>
        public bool IsClosed { get; set; }

        /// <summary>已轉採購數量（用於判斷是否全部轉採）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal OrderedQty { get; set; }

        /// <summary>
        /// 來源 MRP 執行單號（由 MRP 採購需求自動建立時填入；人工加入的明細為 null）。
        /// 表頭的 <see cref="PurchaseRequest.SourceMrpRunId"/> 只能判斷整張單的來源，
        /// 無法區分「MRP 產生的明細」與「使用者事後手動加入的明細」——重跑對帳只應收斂前者，
        /// 故來源指紋必須下放到明細層。
        ///
        /// 不另設 SourceMrpRunLineSeq：採購需求會跨 BOM 各階以品號彙總成一行
        /// （<c>GroupBy(d =&gt; d.ProductCode)</c>），一行可能同時源自多個 MrpRunLine，
        /// 單一序號無法代表；對帳鍵為 (SourceMrpRunId, ProductCode)，品號本就在明細上。
        /// </summary>
        [MaxLength(30)]
        public string? SourceMrpRunId { get; set; }

        // Navigation
        [ForeignKey(nameof(PurchaseRequestId))]
        public PurchaseRequest? PurchaseRequest { get; set; }
    }
}
