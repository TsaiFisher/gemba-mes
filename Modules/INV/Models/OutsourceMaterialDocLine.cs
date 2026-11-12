using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    public class OutsourceMaterialDocLine : IInventoryDocumentLine
    {
        [Required, MaxLength(30)]
        public string OutsourceMaterialDocId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal? UnitPrice { get; set; }

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [MaxLength(30)]
        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }

        /// <summary>已沖銷數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtySourceCleared { get; set; }

        [ForeignKey(nameof(OutsourceMaterialDocId))]
        public OutsourceMaterialDoc? OutsourceMaterialDoc { get; set; }

        public ICollection<OutsourceMaterialDocLocationDetail> LocationDetails { get; set; } = new List<OutsourceMaterialDocLocationDetail>();
    }
}
