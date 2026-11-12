using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    public enum JournalEntryType
    {
        Automatic  = 0,  // 業務交易自動產生
        Manual     = 1,  // 手動調整分錄
        Adjustment = 2   // 期末調整（匯率重評、NRV 減損等）
    }

    /// <summary>管理日記帳（Phase C，§4.3）。每筆確認的業務交易自動觸發借貸記錄。</summary>
    public class JournalEntry : IPlantEntity
    {
        [Key, MaxLength(30)]
        public string JournalId { get; set; } = string.Empty;

        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        public DateOnly EntryDate { get; set; }

        [Required, MaxLength(50)]
        public string SourceDocType { get; set; } = string.Empty; // GoodsReceipt / APPayment / ...

        [MaxLength(30)]
        public string? SourceDocId { get; set; }

        public JournalEntryType Type { get; set; } = JournalEntryType.Automatic;

        [MaxLength(500)]
        public string? Description { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }

        public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();
    }

    public class JournalLine
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string JournalId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(20)]
        public string AccountCode { get; set; } = string.Empty;

        [MaxLength(100)]
        public string AccountName { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal Debit { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Credit { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Amount { get; set; }         // 外幣金額

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1m;

        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }   // 本位幣

        [MaxLength(50)]
        public string? CostCenter { get; set; }

        /// <summary>行級來源維度（§9 P-2 D2-A）：期間彙總分錄逐來源拆行時的來源單號（如工單號），
        /// 供逐工單成本回溯；表頭 SourceDocId 為期間/彙總鍵時由此補工單維度。</summary>
        [MaxLength(50)]
        public string? SourceRef { get; set; }

        [MaxLength(200)]
        public string? Description { get; set; }

        [ForeignKey(nameof(JournalId))]
        public JournalEntry? JournalEntry { get; set; }
    }
}
