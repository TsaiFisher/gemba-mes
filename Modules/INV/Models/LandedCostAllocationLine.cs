using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>採購分攤費用分攤單明細（規格 §3.4）。每行對應一筆來源進貨明細。</summary>
    public class LandedCostAllocationLine
    {
        [Required, MaxLength(30)]
        public string LandedCostAllocationId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>來源進貨單號 → PUR.GoodsReceipt</summary>
        [Required, MaxLength(30)]
        public string GoodsReceiptId { get; set; } = string.Empty;

        /// <summary>來源進貨明細序號</summary>
        public int GoodsReceiptLineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>該進貨明細收貨量（庫存單位，快照）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceiptQty { get; set; }

        /// <summary>採購金額（金額基準權重，快照自進貨明細 LineAmount）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal PurchaseAmount { get; set; }

        /// <summary>重量（重量基準權重 = ReceiptQty × Product.NetWeight，快照）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Weight { get; set; }

        /// <summary>分攤前入庫單價（快照）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal OriginalUnitCost { get; set; }

        /// <summary>分攤到本行的外包費用（= Capitalized + Expensed）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AllocatedCost { get; set; }

        /// <summary>資本化部分（依在庫餘量比例滾入存貨）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal CapitalizedCost { get; set; }

        /// <summary>費用化部分（已出庫對應，轉 PPV/費用）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ExpensedCost { get; set; }

        /// <summary>在庫餘量套用資本化後之新均價（快照，Quantity ≤ 0 時維持原值）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal NewUnitCost { get; set; }

        /// <summary>
        /// 確認當下該料號儲位批號的累計出庫量（方向 −1 的異動數量合計）。作廢時現值大於它＝確認後有出庫，擋下。
        /// 用快照而不用時間比對：伺服器時鐘校正會回撥，建立時間排不出先後（2026-10-23 實測）。
        /// 確認早於此欄上線的舊單為 null，作廢時退回以傳票建立時間判斷。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? OutflowQtyAtConfirm { get; set; }

        /// <summary>確認當下該料號儲位批號的異動筆數；FIFO 品有任何新異動（新成本層）即不可作廢。</summary>
        public int? MovementCountAtConfirm { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [ForeignKey(nameof(LandedCostAllocationId))]
        public LandedCostAllocation? LandedCostAllocation { get; set; }
    }
}
