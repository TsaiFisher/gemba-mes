using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 派工發料 — 由 WorkOrderMaterial 帶入，發料/領料依此扣帳
    /// （委外沿用此表，mfg-domain.md §10-4）。含 UomCode 發料單位。
    /// </summary>
    public class DispatchMaterial
    {
        [Required, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        public int BomSeq { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>需發料量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal RequiredQty { get; set; }

        /// <summary>已發料量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal IssuedQty { get; set; } = 0;

        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        public Dispatch? Dispatch { get; set; }
    }
}
