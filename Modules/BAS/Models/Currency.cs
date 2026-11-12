using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>幣別（CRN）</summary>
    public class Currency
    {
        [Key, MaxLength(20)]
        public string CurrencyCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string CurrencyName { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? ShortName { get; set; }

        /// <summary>貨幣符號，例：$、€、NT$</summary>
        [MaxLength(10)]
        public string? Symbol { get; set; }

        /// <summary>小數位數（上限見 <see cref="Services.DecimalPrecision.MaxStorable"/>）</summary>
        [Range(0, Services.DecimalPrecision.MaxStorable)]
        public int DecimalPlaces { get; set; } = 2;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public ICollection<CurrencyRate> Rates { get; set; } = new List<CurrencyRate>();
    }
}
