using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>進貨單明細（對應舊 PE_D 表）</summary>
    public class GoodsReceiptLine
    {
        [Required, MaxLength(30)]
        public string GoodsReceiptId { get; set; } = string.Empty;

        /// <summary>明細序號（1, 2, 3...）</summary>
        public int LineNo { get; set; }

        /// <summary>來源採購單號（可為空 = 直接進貨）</summary>
        [MaxLength(30)]
        public string? PurchaseOrderId { get; set; }

        /// <summary>來源採購明細序號</summary>
        public int? PoLineNo { get; set; }

        /// <summary>品號 → FK to Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>進貨數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceiptQty { get; set; }

        /// <summary>進貨單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? UomCode { get; set; }

        /// <summary>庫存換算數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        /// <summary>庫存單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        /// <summary>單價（採購幣別）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 存貨計價與暫估掛帳用的單價（採購幣別），建單／存檔時由伺服器決定並凍結。
        ///
        /// 與 <see cref="UnitPrice"/> 的分工：<see cref="UnitPrice"/> 是**這批貨要付多少**
        /// （應付認列的基礎，收貨當下可改），本欄是**這批貨值多少**（《對帳規格》§1.1＝採購單價）。
        /// 兩者的差額就是進料差異，由應付認列端落位。
        ///
        /// 為什麼要存下來而不是上架時現查採購單：已核准的採購單仍可修改單價
        /// （<c>PurchaseOrderStateMachine.CanEdit</c> 只擋審核中），而上架是逐批的——
        /// 現查會讓「先上架一部分、改了採購價、再上架剩下」的同一批貨進兩個成本，
        /// 且事後無從得知當初用的是哪一個價。凍結之後，計價基礎與暫估掛帳金額
        /// 一樣是有憑證的記錄值，對帳兩端才同源。
        ///
        /// 可為空：本欄之前的既有資料沒有這個欄位，讀取端在為 null 時回退為現查
        /// （見 <c>GoodsReceiptCostBasis</c>），故不需要回填。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? AccrualUnitPrice { get; set; }

        /// <summary>採購幣別 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>匯率</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        /// <summary>稅別 → FK to Tax</summary>
        [MaxLength(20)]
        public string? TaxCode { get; set; }

        /// <summary>稅率</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>未稅金額（採購幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        /// <summary>稅額（採購幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>含稅總額（採購幣，未換算本幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣含稅總額（TotalAmount × ExchangeRate）。混幣單據的表頭合計一律加總本欄。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>入庫倉庫代號 → FK to Warehouse</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        /// <summary>入庫儲位代號 → FK to StorageLocation</summary>
        [MaxLength(20)]
        public string? LocationCode { get; set; }

        /// <summary>批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>倉庫已處理量（= Σ LocationDetails.Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }
        
        /// <summary>累計退貨數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReturnedQty { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>已沖銷數量 (用於追蹤後續單據沖抵量)</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtySourceCleared { get; set; }

        // ── 委外進貨 QC 拆量（mfg-domain.md §19 Stage 5b）─────────────────────

        /// <summary>委外進貨：良品數量（正常入庫）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyGood { get; set; }

        /// <summary>委外進貨：不良品數量（觸發重工工單）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyRejected { get; set; }

        // Navigation
        [ForeignKey(nameof(GoodsReceiptId))]
        public GoodsReceipt? GoodsReceipt { get; set; }

        public ICollection<GoodsReceiptLocationDetail> LocationDetails { get; set; } = new List<GoodsReceiptLocationDetail>();
    }
}
