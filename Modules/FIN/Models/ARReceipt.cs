using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public enum ARReceiptStatus
    {
        Draft    = 0,
        Pending  = 1,
        Approved = 2,
        Received = 3,
        Void     = 4
    }

    /// <summary>應收帳款收款單（Phase A）。走簽核流：Draft→Submit→Approve→Confirm（入帳）</summary>
    public class ARReceipt : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string ARReceiptId { get; set; } = string.Empty;

        public DateOnly ReceiptDate { get; set; }

        [Required, MaxLength(20)]
        public string CustomerCode { get; set; } = string.Empty;

        public PaymentMethod Method { get; set; } = PaymentMethod.Wire;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        [Column(TypeName = "decimal(18,4)")]
        public decimal Amount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal ExchangeGainLoss { get; set; }

        [MaxLength(30)]
        public string? BankAccountId { get; set; }

        [MaxLength(200)]
        public string? Reference { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public ARReceiptStatus Status { get; set; } = ARReceiptStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<ARReceiptAllocation> Allocations { get; set; } = new List<ARReceiptAllocation>();
    }
}
