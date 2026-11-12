using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public enum APInvoiceStatus
    {
        Open        = 0,
        PartialPaid = 1,
        Paid        = 2,
        Void        = 3
    }

    /// <summary>
    /// 應付帳款發票主檔（Phase A）。
    /// 由 GoodsReceipt 或 OutsourceReceipt Confirm 後自動產生；亦可手動建立。
    /// (SourceDocType, SourceDocId) 有唯一索引，防止同單據重複開 Invoice。
    /// </summary>
    public class APInvoice : IPlantEntity, IDataAuthEntity, IErpSyncDocument
    {
        /// <summary>發票號（格式：API{yyyy}{00000}）</summary>
        [Key, MaxLength(30)]
        public string APInvoiceId { get; set; } = string.Empty;

        /// <summary>廠商代號 → FK to Company（IsSupplier）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        public DateOnly InvoiceDate { get; set; }

        /// <summary>到期日（付款期限）</summary>
        public DateOnly DueDate { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>含稅總額（交易幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣金額（TotalAmount × ExchangeRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>已付金額（累計核銷，APPaymentAllocation 更新）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal PaidAmount { get; set; }

        /// <summary>
        /// 已折讓金額（累計，由 <see cref="APDebitNote"/> 確認時累加）。
        /// 未結餘額＝<c>TotalAmount − PaidAmount − DebitedAmount</c>。
        /// 與 AR 側同一個設計，理由見 <c>ARInvoice.CreditedAmount</c>。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal DebitedAmount { get; set; }

        public APInvoiceStatus Status { get; set; } = APInvoiceStatus.Open;

        /// <summary>來源單據類型：GoodsReceipt / OutsourceReceipt / Manual</summary>
        [MaxLength(50)]
        public string? SourceDocType { get; set; }

        /// <summary>來源單據號</summary>
        [MaxLength(30)]
        public string? SourceDocId { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        // IErpSyncEntity（§5.4）
        /// <summary>
        /// 樂觀並發權杖（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken，零 DDL）。
        ///
        /// 核銷是「讀未結餘額 → 驗證 → 累加 PaidAmount」的讀改寫：兩張付款單同時確認同一張
        /// 發票時，各自讀到 PaidAmount = 0 而都通過「不超出未結餘額」的驗證，接著各自寫入
        /// 絕對值（+= 是在自己的快照上算的）——後到者覆蓋先到者，發票上只認一次付款，
        /// 但銀行帳戶被扣了兩次、兩張付款單都是 Paid。帳面完全自洽，只有對帳時才會發現。
        /// 掛上 xmin 後，後到者的 UPDATE 帶 WHERE xmin 比對 0 列 → DbUpdateConcurrencyException。
        /// </summary>
        public uint xmin { get; set; }

        public ErpSyncStatus ErpSyncStatus   { get; set; } = ErpSyncStatus.Pending;
        public string?       ErpVoucherNo    { get; set; }
        public DateTime?     ErpSyncTime     { get; set; }
        public string?       ErpErrorMessage { get; set; }
        public int           ErpRetryCount   { get; set; }

        public ICollection<APInvoiceLine> Lines { get; set; } = new List<APInvoiceLine>();
        public ICollection<APPaymentAllocation> Allocations { get; set; } = new List<APPaymentAllocation>();
    }
}
