using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public record PaymentAllocationLine(string InvoiceId, decimal Amount);

    public record APPaymentRequest(
        string SupplierCode,
        DateOnly PaymentDate,
        PaymentMethod Method,
        string? CurrencyCode,
        decimal ExchangeRate,
        decimal Amount,
        string? BankAccountId,
        string? Reference,
        string? Remarks,
        IList<PaymentAllocationLine> Allocations);

    public record ARReceiptRequest(
        string CustomerCode,
        DateOnly ReceiptDate,
        PaymentMethod Method,
        string? CurrencyCode,
        decimal ExchangeRate,
        decimal Amount,
        string? BankAccountId,
        string? Reference,
        string? Remarks,
        IList<PaymentAllocationLine> Allocations);

    public interface IFinPaymentService
    {
        /// <summary>
        /// 建立付款單（Draft）。Confirm 後才扣帳/核銷。
        /// </summary>
        Task<APPayment> CreateAPPaymentAsync(APPaymentRequest req, string userId, string plantId, CancellationToken ct = default);

        /// <summary>Confirm 付款：核銷 APInvoice，扣減 BankAccount.Balance，計算匯兌損益</summary>
        Task ConfirmAPPaymentAsync(string paymentId, CancellationToken ct = default);

        Task VoidAPPaymentAsync(string paymentId, CancellationToken ct = default);

        Task<ARReceipt> CreateARReceiptAsync(ARReceiptRequest req, string userId, string plantId, CancellationToken ct = default);

        Task ConfirmARReceiptAsync(string receiptId, CancellationToken ct = default);

        Task VoidARReceiptAsync(string receiptId, CancellationToken ct = default);
    }
}
