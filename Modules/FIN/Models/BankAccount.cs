using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>銀行帳戶主檔（廠別主檔）</summary>
    public class BankAccount : IPlantEntity
    {
        [Key, MaxLength(30)]
        public string AccountId { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string BankName { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string AccountNo { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>即時餘額（每筆 APPayment/ARReceipt Confirm 後更新，xmin 樂觀鎖）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Balance { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(200)]
        public string? Remarks { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // xmin 樂觀鎖（防並發餘額覆蓋）— 在 Fluent API 設定 IsRowVersion / UseXminAsConcurrencyToken
        public uint xmin { get; set; }
    }
}
