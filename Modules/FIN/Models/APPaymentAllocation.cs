using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>付款核銷明細：一筆付款可核銷多張 APInvoice（部分付款）</summary>
    public class APPaymentAllocation
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string APPaymentId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string APInvoiceId { get; set; } = string.Empty;

        /// <summary>本次核銷金額（交易幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AllocatedAmount { get; set; }

        /// <summary>本次核銷本幣金額</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AllocatedAmountLocal { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [ForeignKey(nameof(APPaymentId))]
        public APPayment? APPayment { get; set; }

        [ForeignKey(nameof(APInvoiceId))]
        public APInvoice? APInvoice { get; set; }
    }
}
