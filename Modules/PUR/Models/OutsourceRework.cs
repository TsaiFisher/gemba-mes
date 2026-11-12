using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>委外重工責任歸屬（pur-domain.md §4.2 成本模型）。</summary>
    public enum ReworkLiabilityParty
    {
        Vendor = 0,   // 廠商責任（工藝不良）→ 向廠商扣款/索賠
        Us     = 1,   // 我方責任（來料/規格）→ 應付重工加工費
        Shared = 2    // 共同 → 依 VendorLiablePct 拆分
    }

    /// <summary>委外重工單狀態。</summary>
    public enum OutsourceReworkStatus
    {
        Draft    = 0,  // 草稿
        Sent     = 1,  // 已送件（送回委外廠重工）
        Received = 2,  // 已收回
        Void     = 3   // 作廢
    }

    /// <summary>
    /// 委外重工單（Phase F，pur-domain.md §4.2）。
    /// 委外進貨 IQC Failed（NCR 處置「退件重工」）或人工建立時產生，記錄重工數量與
    /// <b>成本/索賠歸屬</b>（責任歸屬 + 雙路徑，2026-07-09 定案）：
    /// 廠商責任→<see cref="VendorChargebackAmt"/> 向廠商扣款（沿用 OutsourceReceiptLine.ExcessScrapPenalty 精神）；
    /// 我方責任→<see cref="ReworkProcessingFee"/> 應付重工加工費；共同→依 <see cref="VendorLiablePct"/> 拆分。
    /// 跨模組引用（Dispatch / OutsourcePurchaseOrder / Ncr）僅存字串、應用層驗證，不設 EF 導航。
    /// </summary>
    public class OutsourceRework : IPlantEntity, IDataAuthEntity
    {
        /// <summary>委外重工單號（採番 DocType=OutsourceRework，前綴 RW）</summary>
        [Key, MaxLength(30)]
        public string OutsourceReworkId { get; set; } = string.Empty;

        /// <summary>原始委外派工 → MFG.Dispatch（僅字串，應用層驗證）</summary>
        [MaxLength(30)]
        public string? OriginalDispatchId { get; set; }

        /// <summary>重工用新建委外採購單 → MFG.OutsourcePurchaseOrder（僅字串，可後補）</summary>
        [MaxLength(30)]
        public string? OutsourcePurchaseOrderId { get; set; }

        /// <summary>定責來源 → QCR.Ncr（IQC Failed 之 NCR，僅字串）</summary>
        [MaxLength(30)]
        public string? SourceNcrId { get; set; }

        /// <summary>重工數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReworkQty { get; set; }

        /// <summary>重工原因</summary>
        [MaxLength(500)]
        public string? ReworkReason { get; set; }

        /// <summary>責任歸屬（決定成本流向）</summary>
        public ReworkLiabilityParty LiabilityParty { get; set; } = ReworkLiabilityParty.Vendor;

        /// <summary>重工成本全額（扣款計算基礎；VendorChargebackAmt＝本欄 × 廠商占比）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReworkCost { get; set; }

        /// <summary>重工加工費全額（應付計算基礎；ReworkProcessingFee＝本欄 × 我方占比）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ProcessingFee { get; set; }

        /// <summary>Shared 時廠商分攤比例（0–100；Vendor=100、Us=0，由服務層依歸屬設定）</summary>
        [Column(TypeName = "decimal(5,2)")]
        public decimal VendorLiablePct { get; set; }

        /// <summary>向廠商扣款/索賠金額（廠商責任占比 × 重工成本）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal VendorChargebackAmt { get; set; }

        /// <summary>我方應付重工加工費（我方責任占比 × 加工費）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReworkProcessingFee { get; set; }

        /// <summary>單據狀態</summary>
        public OutsourceReworkStatus Status { get; set; } = OutsourceReworkStatus.Draft;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>建單人 UserId</summary>
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // 廠別隔離 + 資料授權
        public string? PlantId { get; set; }
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }
    }
}
