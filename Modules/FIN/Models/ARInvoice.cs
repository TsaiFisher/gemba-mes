using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public enum ARInvoiceStatus
    {
        Open            = 0,
        PartialReceived = 1,
        Received        = 2,
        Void            = 3
    }

    /// <summary>
    /// 應收帳款發票主檔（Phase A）。
    /// 由 GoodsShipment Confirm 後自動產生；亦可手動建立。
    /// </summary>
    public class ARInvoice : IPlantEntity, IDataAuthEntity, IErpSyncDocument
    {
        [Key, MaxLength(30)]
        public string ARInvoiceId { get; set; } = string.Empty;

        /// <summary>客戶代號 → FK to Company（IsCustomer）</summary>
        [Required, MaxLength(20)]
        public string CustomerCode { get; set; } = string.Empty;

        public DateOnly InvoiceDate { get; set; }

        public DateOnly DueDate { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>已收金額（累計核銷，ARReceiptAllocation 更新）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceivedAmount { get; set; }

        /// <summary>
        /// 已折讓金額（累計，由 <see cref="ARCreditNote"/> 確認時累加）。
        ///
        /// 未結餘額的正確算式因此是 <c>TotalAmount − ReceivedAmount − CreditedAmount</c>。
        /// 之所以另立一欄而不是併進 <see cref="ReceivedAmount"/>：折讓**不是收到錢**，
        /// 併進去會讓「客戶到底付了多少」這個問題再也答不出來，
        /// 而那正是收款對帳與呆帳判斷的依據。
        ///
        /// 也不是去改 <see cref="TotalAmount"/>：發票一旦開出去並申報過，
        /// 金額就不能再動——折讓在稅務上是另一張憑證，不是把原憑證改小。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal CreditedAmount { get; set; }

        public ARInvoiceStatus Status { get; set; } = ARInvoiceStatus.Open;

        /// <summary>來源單據類型：GoodsShipment / Manual</summary>
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
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        // IErpSyncEntity（§9 P-3，reconciliation-decisions.md D3 最小範圍：與 APInvoice 對稱）
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

        public ICollection<ARInvoiceLine> Lines { get; set; } = new List<ARInvoiceLine>();
        public ICollection<ARReceiptAllocation> Allocations { get; set; } = new List<ARReceiptAllocation>();
    }
}
