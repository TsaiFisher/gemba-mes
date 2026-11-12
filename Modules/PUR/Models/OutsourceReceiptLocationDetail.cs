using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>委外收貨單儲位明細（第3層：倉庫作業時建立）</summary>
    public class OutsourceReceiptLocationDetail
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string OutsourceReceiptId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public OutsourceReceiptLine? Line { get; set; }
    }
}
