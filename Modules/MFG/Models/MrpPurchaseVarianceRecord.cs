using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>差異項的追蹤狀態。</summary>
    public enum MrpVarianceStatus
    {
        /// <summary>未結清，仍需人工處理或確認。</summary>
        Open = 0,

        /// <summary>已結清（自動結案退場，或人工確認處理完畢）。</summary>
        Resolved = 1
    }

    /// <summary>
    /// MRP 重跑採購對帳差異項（mfg-domain.md §10-5 P2）。
    ///
    /// P1 的差異清單只存在於當次 <see cref="MrpRunResult"/>——使用者關掉視窗就消失，
    /// 與它取代的那句 flash warning 有同樣的問題。本表把差異落地成「必須被結清的待辦」，
    /// 由決策清單畫面持續呈現，這才是「重跑後有品項落差未處理」不再石沉大海的關鍵。
    ///
    /// 每次對帳會先刪除同一執行單既有的 <see cref="MrpVarianceStatus.Open"/> 記錄再重建
    /// （差異是由最新一次展開推導出來的，不累積歷史雜訊）；已 Resolved 的保留供追溯。
    ///
    /// 唯讀待辦記錄、非使用者輸入表單，值型別屬性以 [Display] 標註排除於 R5b 之外。
    /// </summary>
    public class MrpPurchaseVarianceRecord : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(40)]
        public string VarianceId { get; set; } = Guid.NewGuid().ToString();

        [Required, MaxLength(30)]
        public string MrpRunId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>關聯請購單號（Added 類型時為空字串）。</summary>
        [MaxLength(30)]
        public string PurchaseRequestId { get; set; } = string.Empty;

        /// <summary>關聯請購明細序號（Added 類型時為 0）。</summary>
        [Display(Name = "Variance Line No")]
        public int LineNo { get; set; }

        [Display(Name = "Variance Kind")]
        public MrpPurchaseVarianceKind Kind { get; set; }

        [Display(Name = "Variance Disposition")]
        public MrpPurchaseVarianceDisposition Disposition { get; set; }

        [Display(Name = "Variance Status")]
        public MrpVarianceStatus Status { get; set; } = MrpVarianceStatus.Open;

        [Display(Name = "Variance Previous Qty")]
        [Column(TypeName = "decimal(18,4)")]
        public decimal PreviousQty { get; set; }

        [Display(Name = "Variance Current Qty")]
        [Column(TypeName = "decimal(18,4)")]
        public decimal CurrentQty { get; set; }

        [MaxLength(1000)]
        public string Description { get; set; } = string.Empty;

        [Display(Name = "Variance Detected At")]
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? ResolvedBy { get; set; }

        public DateTime? ResolvedAt { get; set; }

        [MaxLength(500)]
        public string? ResolutionNote { get; set; }

        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }
    }
}
