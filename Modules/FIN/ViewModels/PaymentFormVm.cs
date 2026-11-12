using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN.ViewModels
{
    public class APPaymentFormVm
    {
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        public PaymentMethod Method { get; set; } = PaymentMethod.Wire;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Range(0.000001, double.MaxValue)]
        public decimal ExchangeRate { get; set; } = 1m;

        // 不限正數：退款（負）與純抵用（0）都合法；金額必須等於核銷合計，由 FinPaymentService 驗證。
        public decimal Amount { get; set; }

        [MaxLength(30)]
        public string? BankAccountId { get; set; }

        [MaxLength(200)]
        public string? Reference { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<AllocationItemVm> Allocations { get; set; } = new();
    }

    public class ARReceiptFormVm
    {
        [Required, MaxLength(20)]
        public string CustomerCode { get; set; } = string.Empty;

        public DateOnly ReceiptDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        public PaymentMethod Method { get; set; } = PaymentMethod.Wire;

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Range(0.000001, double.MaxValue)]
        public decimal ExchangeRate { get; set; } = 1m;

        // 不限正數：退款（負）與純抵用（0）都合法；金額必須等於核銷合計，由 FinPaymentService 驗證。
        public decimal Amount { get; set; }

        [MaxLength(30)]
        public string? BankAccountId { get; set; }

        [MaxLength(200)]
        public string? Reference { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<AllocationItemVm> Allocations { get; set; } = new();
    }

    public class AllocationItemVm
    {
        public string InvoiceId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }
}
