using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>幣別每日匯率（CRN_D）— 複合主鍵：CurrencyCode + RateDate</summary>
    public class CurrencyRate
    {
        [Required, MaxLength(20)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>匯率日期</summary>
        public DateOnly RateDate { get; set; }

        /// <summary>買入匯率</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal BuyRate { get; set; }

        /// <summary>賣出匯率</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal SellRate { get; set; }

        // Navigation
        [ForeignKey(nameof(CurrencyCode))]
        public Currency? Currency { get; set; }
    }
}
