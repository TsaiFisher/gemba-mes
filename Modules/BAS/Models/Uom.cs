using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>單位（UM）</summary>
    public class Uom
    {
        [Key, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string UomName { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Remarks { get; set; }

        /// <summary>小數位數（上限見 <see cref="Services.DecimalPrecision.MaxStorable"/>）</summary>
        [Range(0, Services.DecimalPrecision.MaxStorable)]
        public int DecimalPlaces { get; set; } = 0;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public ICollection<UomConversion> Conversions { get; set; } = new List<UomConversion>();
    }
}
