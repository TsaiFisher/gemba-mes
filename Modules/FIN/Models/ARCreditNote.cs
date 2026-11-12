using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public enum CreditNoteStatus
    {
        Draft  = 0,
        Posted = 1,
        Void   = 2,
    }

    /// <summary>
    /// 銷貨折讓單（W3-F3 Phase 2）。
    ///
    /// 存在的理由：出貨開了 AR 發票、客戶已（部分）付款之後才發生銷退。
    /// <c>ARInvoiceService.ReverseForReturnedQtyAsync</c> 對已結清的發票**刻意不動金額**
    /// ——改一張已經開出去、已經申報過的發票，會讓憑證與稅務申報前後不一致。
    /// 但貨確實退回來了，錢還掛在應收上，公司實際上欠客戶一筆錢；Phase 1 把這個缺口
    /// 算出來並由 <c>RETURN_SUBLEDGER_UNREVERSED</c> 擋住關帳，卻**沒有給出口**：
    /// 稽核叫了，系統裡沒有任何單據可以把它處理掉。這張單就是那個出口。
    ///
    /// 為什麼不是「負向 AR 發票」（重用 ARInvoice 存負數）：負數會流進每一支
    /// 假設「金額為正」的邏輯——<c>Qty &gt; 0</c> 驗證、`Received` 的判準
    /// （<c>ReceivedAmount &gt;= TotalAmount</c>，負數發票一生下來就是已收）、
    /// 核銷上限、帳齡分帳——而且每一處都是**靜默**壞掉。獨立單據把影響面收斂成
    /// 一個可以 grep 的欄位（<c>ARInvoice.CreditedAmount</c>）。
    ///
    /// 它在子帳裡的表現是**非現金核銷**：確認時把原發票的未結餘額沖掉
    /// （<c>CreditedAmount</c> 累加），但**不動發票的 TotalAmount**——
    /// 憑證金額不變，帳齡自動反映，稅務申報上是一張獨立的折讓憑證。
    /// </summary>
    public class ARCreditNote : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string ARCreditNoteId { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string CustomerCode { get; set; } = string.Empty;

        public DateOnly NoteDate { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        public CreditNoteStatus Status { get; set; } = CreditNoteStatus.Draft;

        /// <summary>來源單據類型：SalesReturn（目前唯一來源，由退貨過帳自動產生草稿）</summary>
        [MaxLength(50)]
        public string? SourceDocType { get; set; }

        [MaxLength(30)]
        public string? SourceDocId { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<ARCreditNoteLine> Lines { get; set; } = new List<ARCreditNoteLine>();
    }

    /// <summary>銷貨折讓單明細：一行對應「原發票的哪一行」被折讓了多少。</summary>
    public class ARCreditNoteLine
    {
        [Required, MaxLength(30)]
        public string ARCreditNoteId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>被折讓的原始應收發票。</summary>
        [Required, MaxLength(30)]
        public string ARInvoiceId { get; set; } = string.Empty;

        /// <summary>原發票的行號——**以行號對應而非品號**，同品號多行時品號比對會固定命中第一行。</summary>
        public int InvoiceLineNo { get; set; }

        [MaxLength(30)]
        public string? ProductCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [ForeignKey(nameof(ARCreditNoteId))]
        public ARCreditNote? ARCreditNote { get; set; }
    }
}
