using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public enum PaymentMethod
    {
        Cash   = 0,
        Wire   = 1,
        Cheque = 2,
        Bill   = 3
    }

    public enum APPaymentStatus
    {
        Draft   = 0,
        Pending = 1,   // 待審核
        Approved = 2,  // 已審核，待付款
        Paid    = 3,   // 已付款（Confirm）
        Void    = 4
    }

    /// <summary>應付帳款付款單（Phase A）。走簽核流：Draft→Submit→Approve→Confirm（扣帳）</summary>
    public class APPayment : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string APPaymentId { get; set; } = string.Empty;

        public DateOnly PaymentDate { get; set; }

        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        public PaymentMethod Method { get; set; } = PaymentMethod.Wire;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        /// <summary>付款金額（交易幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Amount { get; set; }

        /// <summary>本位幣金額（Amount × ExchangeRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>已實現匯兌損益（Confirm 時計算：AmountLocal vs Invoice 帳面本幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ExchangeGainLoss { get; set; }

        /// <summary>銀行帳戶 → FK to BankAccount</summary>
        [MaxLength(30)]
        public string? BankAccountId { get; set; }

        [MaxLength(200)]
        public string? Reference { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public APPaymentStatus Status { get; set; } = APPaymentStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<APPaymentAllocation> Allocations { get; set; } = new List<APPaymentAllocation>();
    }
}
