using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 單位換算（UM_D）— 複合主鍵：FromUomCode + ToUomCode
    /// 換算公式：目標數量 = 來源數量 × (Numerator / Denominator)
    /// 例：1 KG = 1000 G → FromUom=KG, ToUom=G, Numerator=1000, Denominator=1
    /// </summary>
    public class UomConversion
    {
        [Required, MaxLength(20)]
        public string FromUomCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string ToUomCode { get; set; } = string.Empty;

        /// <summary>分子</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Numerator { get; set; } = 1;

        /// <summary>分母</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Denominator { get; set; } = 1;

        // Navigation
        [ForeignKey(nameof(FromUomCode))]
        public Uom? FromUom { get; set; }
    }
}
