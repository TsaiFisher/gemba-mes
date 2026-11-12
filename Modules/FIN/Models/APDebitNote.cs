using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>
    /// 進貨折讓單（W3-F3 Phase 2，AP 側，與 <see cref="ARCreditNote"/> 完全對稱）。
    ///
    /// 觸發情境：進貨已認列 AP 發票、款已（部分）付出，之後才發生進貨退出。
    /// <c>APInvoiceService.ReverseForReturnedQtyAsync</c> 同樣刻意不動已結清發票的金額，
    /// 缺口由 <c>RETURN_SUBLEDGER_UNREVERSED</c> 擋著關帳而沒有出口。
    ///
    /// 命名照會計慣例：減少應付的憑證是 debit note（我方借記應付），
    /// 減少應收的是 credit note；兩者方向相反，故不共用一個型別。
    /// 設計上的所有理由（為什麼不是負向發票、為什麼不動 TotalAmount）
    /// 見 <see cref="ARCreditNote"/> 的說明，此處不重複。
    /// </summary>
    public class APDebitNote : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string APDebitNoteId { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

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

        /// <summary>來源單據類型：GoodsReturn（由進貨退出過帳自動產生草稿）</summary>
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

        public ICollection<APDebitNoteLine> Lines { get; set; } = new List<APDebitNoteLine>();
    }

    /// <summary>進貨折讓單明細：一行對應「原發票的哪一行」被折讓了多少。</summary>
    public class APDebitNoteLine
    {
        [Required, MaxLength(30)]
        public string APDebitNoteId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string APInvoiceId { get; set; } = string.Empty;

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

        [ForeignKey(nameof(APDebitNoteId))]
        public APDebitNote? APDebitNote { get; set; }
    }
}
