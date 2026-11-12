using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>進貨單儲位明細（第3層：倉庫作業時建立）</summary>
    public class GoodsReceiptLocationDetail
    {
        public int Id { get; set; }

        /// <summary>所屬進貨單號</summary>
        [Required, MaxLength(30)]
        public string GoodsReceiptId { get; set; } = string.Empty;

        /// <summary>所屬明細序號</summary>
        public int LineNo { get; set; }

        /// <summary>儲位代號（全域唯一）→ FK to StorageLocation</summary>
        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = string.Empty;

        /// <summary>倉庫代號（由 LocationCode 自動回寫）</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        /// <summary>批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>數量（庫存單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public GoodsReceiptLine? Line { get; set; }
    }
}
