using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ORD.Models
{
    public class InternalOrderLine
    {
        [MaxLength(30)]
        public string InternalOrderId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal OrderQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        public bool IsClosed { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public InternalOrder? InternalOrder { get; set; }
        public ICollection<InternalOrderLocationDetail> LocationDetails { get; set; } = new List<InternalOrderLocationDetail>();
    }
}
