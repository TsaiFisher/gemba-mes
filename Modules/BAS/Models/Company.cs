using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 供應商資格狀態（pur-domain.md §3）。
    /// 用於採購/委外下單時顯示警示；完整評核功能見 Phase G VendorEvaluation。
    /// </summary>
    public enum SupplierQualificationStatus
    {
        Unqualified = 0,  // 未評核（預設）
        Qualified   = 1,  // 合格
        Probation   = 2,  // 觀察期
        Suspended   = 3,  // 暫停（採購/委外下單時顯示紅色警示）
    }

    /// <summary>來往公司（客戶 / 供應商）</summary>
    public class Company
    {
        [Key, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string CompanyName { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? ShortName { get; set; }

        /// <summary>是否為客戶</summary>
        public bool IsCustomer { get; set; }

        /// <summary>是否為供應商</summary>
        public bool IsSupplier { get; set; }

        /// <summary>收款方式 → CodeEntry: payment_type</summary>
        [MaxLength(20)]
        public string? PaymentType { get; set; }

        /// <summary>幣別代號 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>稅別代號 → FK to Tax</summary>
        [MaxLength(20)]
        public string? TaxCode { get; set; }

        /// <summary>營業稅率（自動從稅別填入，可覆寫）</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>
        /// 結帳日（每月第幾日，例：5 = 每月 5 日；31 在小月視為月底）。有填＝月結客戶／廠商：
        /// 單據日落在哪一期，就以該期結帳日起算到期日。欄位名是歷史命名，語意是「日」不是「天數」。
        /// 到期日的完整規則見 <see cref="SmartFactory.MES.Modules.BAS.Services.PaymentTerms"/>。
        /// </summary>
        public int? PaymentTermDays { get; set; }

        /// <summary>
        /// 收款／付款天數（例：30 = 月結 30 天）。到期日＝起算日＋此天數；未填沿用 30 天。
        /// 起算日：有結帳日者為當期結帳日，否則為單據日。
        /// </summary>
        [Range(0, 365)]
        public int? PaymentCreditDays { get; set; }

        [MaxLength(200)]
        public string? Address { get; set; }

        /// <summary>統一編號 / 稅證編號</summary>
        [MaxLength(50)]
        public string? InvoiceNo { get; set; }

        /// <summary>業務人員 UserId</summary>
        [MaxLength(450)]
        public string? SalesPersonId { get; set; }

        /// <summary>客戶等級 → CodeEntry: abc_grade</summary>
        [MaxLength(10)]
        public string? AbcGrade { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>供應商資格狀態（pur-domain.md §3）；客戶類公司此欄位忽略。</summary>
        public SupplierQualificationStatus QualificationStatus { get; set; } = SupplierQualificationStatus.Unqualified;

        /// <summary>資格到期日（null = 永久有效）；到期後前端提示重新評核。</summary>
        public DateOnly? QualificationExpiry { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public ICollection<CompanyContact> Contacts { get; set; } = new List<CompanyContact>();
    }
}
