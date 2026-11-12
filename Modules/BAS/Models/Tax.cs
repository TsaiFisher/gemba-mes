using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>稅別（TAX）</summary>
    public class Tax
    {
        [Key, MaxLength(20)]
        public string TaxCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string TaxName { get; set; } = string.Empty;

        /// <summary>稅率（例：0.05 = 5%）</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    }
}
