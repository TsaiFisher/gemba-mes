using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>採購單主檔（對應舊 PO 表）</summary>
    public class PurchaseOrder : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        /// <summary>採購單號，由 DocNumberService 產生（格式：PO{yyyy}{00000}）</summary>
        [Key, MaxLength(30)]
        public string PurchaseOrderId { get; set; } = string.Empty;

        /// <summary>採購日期</summary>
        public DateOnly OrderDate { get; set; }

        /// <summary>建單人 UserId</summary>
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>供應商代號 → FK to Company（IsSupplier = true）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>送貨地址</summary>
        [MaxLength(300)]
        public string? DeliveryAddress { get; set; }

        /// <summary>
        /// 貿易條件（Incoterms，CodeEntry: <c>trade_term</c>；可空＝國內交易未約定）。
        ///
        /// <para><b>它不是一個參考欄位，它決定後面那些費用該不該進我們的成本。</b>
        /// 關稅／國際運費／報關費／保險由誰負擔是 Incoterms 講的事：
        /// `EXW`／`FOB` 下這些是**我們的**，要經進料費用分攤單（`LandedCostAllocation`）
        /// 資本化進存貨；`CIF` 只有關稅與報關費是我們的；`DDP` 則**一毛都不該**
        /// 出現在我們的分攤單裡。</para>
        ///
        /// <para><b>為什麼放表頭而不是明細</b>：它是**契約條款**，下單當下就談定，
        /// 整張單一致。而費用的**金額**刻意不放在單據上——下單時金額還不存在
        /// （實重、匯率、稅則都未定），費用來自承攬業者／報關行／保險公司三個不同對象、
        /// 各自開發票，且一張提單常涵蓋多張單，是 N:M。那是分攤單的職責。</para>
        ///
        /// <para>⚠️ 沒有這一格的時候，開分攤單的人只能靠記憶判斷哪些費用該掛上去，
        /// 而掛錯**不會有任何訊號**：單子照過、成本照算，只是那批料的單價永久多了或少了一截。</para>
        /// </summary>
        [MaxLength(20)]
        public string? TradeTermCode { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        // Navigation
        public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
    }
}
