using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>委外收貨單明細（Phase A）</summary>
    public class OutsourceReceiptLine
    {
        [Required, MaxLength(30)]
        public string OutsourceReceiptId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>來源委外採購單明細序號</summary>
        public int? OpoLineNo { get; set; }

        /// <summary>收回成品品號 → FK to Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>收貨數量（委外採購單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceiptQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        /// <summary>庫存換算數量（庫存單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        /// <summary>委外單價（委外幣別）— 來自委外採購單</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitCost { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        /// <summary>稅別</summary>
        [MaxLength(20)]
        public string? TaxCode { get; set; }

        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>未稅金額（委外幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>含稅總額（委外幣，未換算本幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣含稅總額（TotalAmount × ExchangeRate）。混幣單據的表頭合計一律加總本欄。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>預設入庫倉庫</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        /// <summary>預設入庫儲位</summary>
        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>已過帳數量（= Σ LocationDetails.Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        /// <summary>超耗扣款金額（規格書 §5.2）：實際損耗率超出合約允收損耗率時，
        /// 系統自動計算並從加工費發票中扣減。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ExcessScrapPenalty { get; set; }

        /// <summary>
        /// 本次收貨併入成品成本的**在外原料價值**（本幣，非單價）。
        ///
        /// 委外發料屬 Outsource 類，庫存數量出而總帳不動，料的價值一直留在存貨科目；
        /// 收貨時把該還的那一份併回成品，帳與明細帳才對得起來
        /// （見 <see cref="Services.IOutsourceReceiptSettlementService"/>）。
        /// 存下來是因為分批收貨的結案批要「掃尾」——把還沒併回去的餘額一次併完，
        /// 而餘額 = 淨發料價值 − **前幾批已併回的**；重算一次會在每批的四捨五入上累積誤差，
        /// 一分錢的偏差就足以讓總帳與明細帳對不起來。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal CarriedInMaterialCost { get; set; }

        /// <summary>
        /// 合約允收損耗率（0–1）的快照，收貨存檔時自委外採購單行帶入
        /// （<c>OutsourceReceiptController.NormalizeLinesAsync</c>）。
        ///
        /// <para><b>null ≠ 0</b>：<c>null</c> ＝ 合約未約定損耗條款 → 不扣款
        /// （沒談過的錢不能自己扣）；<c>0</c> ＝ 明確約定零容忍，短交即全額求償。
        /// 原本這個欄位是不可為 null 的 <c>decimal</c>，而**上游根本沒有來源**
        /// （委外採購單與報價都沒有這個欄位、全庫沒有任何寫入點），於是恆為 0，
        /// 而扣款計算被 <c>allowedRate &gt; 0</c> 擋著——超耗扣款在正式環境
        /// 結構性不可達（人工驗收 W2-F14）。</para>
        /// </summary>
        [Range(0, 1)]
        [Column(TypeName = "decimal(8,6)")]
        public decimal? AllowedScrapRate { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        // Navigation
        [ForeignKey(nameof(OutsourceReceiptId))]
        public OutsourceReceipt? OutsourceReceipt { get; set; }

        public ICollection<OutsourceReceiptLocationDetail> LocationDetails { get; set; }
            = new List<OutsourceReceiptLocationDetail>();
    }
}
