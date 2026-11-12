using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>收款核銷明細：一筆收款可核銷多張 ARInvoice（部分收款）</summary>
    public class ARReceiptAllocation
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string ARReceiptId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ARInvoiceId { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal AllocatedAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal AllocatedAmountLocal { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(ARReceiptId))]
        public ARReceipt? ARReceipt { get; set; }

        [ForeignKey(nameof(ARInvoiceId))]
        public ARInvoice? ARInvoice { get; set; }
    }
}
