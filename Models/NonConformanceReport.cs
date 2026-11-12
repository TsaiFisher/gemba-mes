using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    /// <summary>NCR 狀態</summary>
    public static class NcrStatus
    {
        public const string Open              = "Open";
        public const string PendingApproval   = "PendingApproval";
        public const string Approved          = "Approved";
        public const string Executed          = "Executed";
        public const string Closed            = "Closed";
        public const string Void              = "Void";
    }

    /// <summary>不合格品單（NCR）— 對應舊系統 QCR Phase I。
    /// 來源可為 ProductionReport / InspectionOrder / GoodsReceipt / Manual；
    /// 處置方式：Scrap 報廢 / Rework 重工 / UseAsIs 特採 / Return 退回。</summary>
    public class NonConformanceReport : IPlantEntity, IDataAuthEntity
    {
        /// <summary>NCR 單號（由 DocNumberService 產生，格式：NCR{YY}{MM}-{nnnnn}）</summary>
        [Key, MaxLength(30)]
        public string NcrId { get; set; } = string.Empty;

        public string? PlantId { get; set; }

        // ── 來源 ────────────────────────────────────────────────────────────
        /// <summary>ProductionReport | InspectionOrder | GoodsReceipt | Manual</summary>
        [Required, MaxLength(50)]
        public string SourceType { get; set; } = "Manual";

        /// <summary>來源單號（報工 ID、檢驗單號、收貨單號）</summary>
        [MaxLength(60)]
        public string? SourceId { get; set; }

        // ── 品號與數量 ────────────────────────────────────────────────────
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        // ── 不良資訊 ─────────────────────────────────────────────────────
        /// <summary>FK → DefectCode.Code（不良原因）</summary>
        [MaxLength(20)]
        public string? DefectCode { get; set; }

        [MaxLength(1000)]
        public string? RootCause { get; set; }

        [MaxLength(1000)]
        public string? CorrectiveAction { get; set; }

        // ── 處置 ─────────────────────────────────────────────────────────
        /// <summary>Scrap | Rework | UseAsIs | Return | ReturnRework（退件重工，委外 IQC 不合格）</summary>
        [Required, MaxLength(20)]
        public string Disposition { get; set; } = "Scrap";

        /// <summary>Scrap 執行時從此倉庫/儲位扣減庫存</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        // ── 執行結果（Execute 後填入）──────────────────────────────────────
        /// <summary>Scrap 時自動建立的報廢單號</summary>
        [MaxLength(30)]
        public string? ScrapDocId { get; set; }

        /// <summary>Rework 時自動建立的重工工單號</summary>
        [MaxLength(40)]
        public string? ReworkWorkOrderId { get; set; }

        /// <summary>Return（退貨）時自動建立的採購退貨單號（與退貨單雙向連結）</summary>
        [MaxLength(30)]
        public string? GoodsReturnId { get; set; }

        /// <summary>ReturnRework（退件重工）時自動建立的委外重工單號（pur-domain.md §4.2 觸發時機）</summary>
        [MaxLength(30)]
        public string? OutsourceReworkId { get; set; }

        // ── 狀態機 ───────────────────────────────────────────────────────
        [Required, MaxLength(30)]
        public string NcrStatus { get; set; } = Models.NcrStatus.Open;

        // ── 線上簽核（UseAsIs 處置需要上級核准）─────────────────────────
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        [MaxLength(450)]
        public string? ApprovedBy { get; set; }

        public DateTime? ApprovedAt { get; set; }

        // ── 執行記錄 ─────────────────────────────────────────────────────
        [MaxLength(450)]
        public string? ExecutedBy { get; set; }

        public DateTime? ExecutedAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        // ── 稽核欄位 ─────────────────────────────────────────────────────
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId       { get; set; }
        public string? OrganizationUnitPath  { get; set; }
    }
}
