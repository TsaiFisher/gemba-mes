using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN
{
    /// <summary>
    /// FIN 列舉值 → 使用者語系顯示字串。
    /// 查詢列下拉、清單徽章、唯讀表單一律透過本類別取字，避免直接輸出 enum 名稱（英文）。
    /// resx key 全部寫成字面量（不動態串接），維持可靜態檢查。
    /// 對應 key 置於 Resources/ProjectZSharedResource.{zh-TW,en-US}.resx。
    /// </summary>
    public static class FinEnumDisplay
    {
        public static string Text(IStringLocalizer l, APInvoiceStatus s) => s switch
        {
            APInvoiceStatus.Open        => l["APInvoiceStatusOpen"].Value,
            APInvoiceStatus.PartialPaid => l["APInvoiceStatusPartialPaid"].Value,
            APInvoiceStatus.Paid        => l["APInvoiceStatusPaid"].Value,
            APInvoiceStatus.Void        => l["APInvoiceStatusVoid"].Value,
            _                           => s.ToString()
        };

        public static string Text(IStringLocalizer l, ARInvoiceStatus s) => s switch
        {
            ARInvoiceStatus.Open            => l["ARInvoiceStatusOpen"].Value,
            ARInvoiceStatus.PartialReceived => l["ARInvoiceStatusPartialReceived"].Value,
            ARInvoiceStatus.Received        => l["ARInvoiceStatusReceived"].Value,
            ARInvoiceStatus.Void            => l["ARInvoiceStatusVoid"].Value,
            _                               => s.ToString()
        };

        /// <summary>折讓單狀態（AR／AP 共用同一個列舉，故只有一支）。</summary>
        public static string Text(IStringLocalizer l, CreditNoteStatus s) => s switch
        {
            CreditNoteStatus.Draft  => l["CreditNoteStatusDraft"].Value,
            CreditNoteStatus.Posted => l["CreditNoteStatusPosted"].Value,
            CreditNoteStatus.Void   => l["CreditNoteStatusVoid"].Value,
            _                       => s.ToString()
        };

        public static string Text(IStringLocalizer l, APPaymentStatus s) => s switch
        {
            APPaymentStatus.Draft    => l["APPaymentStatusDraft"].Value,
            APPaymentStatus.Pending  => l["APPaymentStatusPending"].Value,
            APPaymentStatus.Approved => l["APPaymentStatusApproved"].Value,
            APPaymentStatus.Paid     => l["APPaymentStatusPaid"].Value,
            APPaymentStatus.Void     => l["APPaymentStatusVoid"].Value,
            _                        => s.ToString()
        };

        public static string Text(IStringLocalizer l, ARReceiptStatus s) => s switch
        {
            ARReceiptStatus.Draft    => l["ARReceiptStatusDraft"].Value,
            ARReceiptStatus.Pending  => l["ARReceiptStatusPending"].Value,
            ARReceiptStatus.Approved => l["ARReceiptStatusApproved"].Value,
            ARReceiptStatus.Received => l["ARReceiptStatusReceived"].Value,
            ARReceiptStatus.Void     => l["ARReceiptStatusVoid"].Value,
            _                        => s.ToString()
        };

        public static string Text(IStringLocalizer l, PaymentMethod m) => m switch
        {
            PaymentMethod.Cash   => l["PaymentMethodCash"].Value,
            PaymentMethod.Wire   => l["PaymentMethodWire"].Value,
            PaymentMethod.Cheque => l["PaymentMethodCheque"].Value,
            PaymentMethod.Bill   => l["PaymentMethodBill"].Value,
            _                    => m.ToString()
        };
    }
}
