using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.ORD.Models
{
    /// <summary>銷售訂單主檔。確認後可依序建立出貨單(GS)及銷退單(GR)。</summary>
    public class SalesOrder : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string SalesOrderId { get; set; } = string.Empty;

        public DateOnly OrderDate { get; set; }

        /// <summary>訂單類型（CodeEntry: PZ.SalesOrderType）</summary>
        [MaxLength(20)]
        public string? OrderType { get; set; }

        /// <summary>客戶代號</summary>
        [Required, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        /// <summary>客戶原訂單號</summary>
        [MaxLength(50)]
        public string? CustomerOrderNo { get; set; }

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        /// <summary>業務員代號（CodeEntry: PZ.SalesRep）</summary>
        [MaxLength(50)]
        public string? SalesRepCode { get; set; }


        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>
        /// 貿易條件（Incoterms，CodeEntry: <c>trade_term</c>；可空＝國內交易未約定）。
        ///
        /// <para><b>出口側與進口側不是同一件事。</b>出口的運費與保險**不資本化**——
        /// 貨在離開，沒有 landed cost 這個概念。依 Incoterms 它要嘛是營業費用（銷貨運費），
        /// 要嘛是收入的減項，而且它還決定**風險移轉時點**，也就是收入認列的時點。
        /// ⚠️ 因此**不要**把 `LandedCostAllocation` 往銷貨側延伸，那是進項專用的。</para>
        ///
        /// <para><b>為什麼放表頭</b>：它是**契約條款**，下單當下就談定，整張單一致。
        /// 對應的進項側欄位在 <c>PurchaseOrder.TradeTermCode</c>。</para>
        ///
        /// <para>⚠️ 出口費用走一般應付與營業費用，**不進分攤單**。</para>
        /// </summary>
        [MaxLength(20)]
        public string? TradeTermCode { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        public ICollection<SalesOrderLine> Lines { get; set; } = new List<SalesOrderLine>();
    }
}
