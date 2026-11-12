using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    public class ReworkMaterialDocLocationDetail
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string ReworkMaterialDocId { get; set; } = string.Empty;

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

        public ReworkMaterialDocLine? Line { get; set; }
    }
}
