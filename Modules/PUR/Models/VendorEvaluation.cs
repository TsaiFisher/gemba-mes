using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>評核結果（pur-domain.md §6.1）</summary>
    public enum EvaluationResult
    {
        Pass        = 0,  // TotalScore ≥ 80 → Qualified
        Conditional = 1,  // 60 ≤ TotalScore < 80 → Probation
        Fail        = 2,  // TotalScore < 60 → Suspended（需主管核准）
    }

    /// <summary>
    /// 合格供應商評分卡（pur-domain.md §6）— 全域，非 IPlantEntity（供應商評核全公司共用，
    /// Quality/Delivery 自動分數彙總所有廠別的進貨/委外收貨）。
    /// 核准後自動回寫 Company.QualificationStatus。
    /// </summary>
    public class VendorEvaluation : Modules._Shared.IApprovalDocument
    {
        public int Id { get; set; }

        /// <summary>廠商代號 FK→Company (IsSupplier)</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>評核年度（如 2026）</summary>
        [Range(2000, 2100)]
        public int EvaluationYear { get; set; }

        /// <summary>評核季度 1–4；null = 年度評核</summary>
        public int? EvaluationQuarter { get; set; }

        /// <summary>品質分數 0–100（進貨檢驗判退量＋NCR 不良量 / 進貨量）</summary>
        [Column(TypeName = "decimal(8,2)")]
        public decimal QualityScore { get; set; }

        /// <summary>
        /// 評核期間內該供應商**有沒有實際進貨**，也就是品質分數算不算得出來。
        ///
        /// <para><c>false</c> 時 <see cref="QualityScore"/> 存的是 100，但那不是評價，
        /// 只是佔位。2026-09-21 之前沒有這個欄位，於是「從沒往來的廠商」與「零不良的廠商」
        /// 在評分卡上長得完全一樣——而後者才是該被加分的那一個。</para>
        /// </summary>
        public bool QualityHasData { get; set; } = true;

        /// <summary>交期分數 0–100（OTD Rate 衍生）</summary>
        [Column(TypeName = "decimal(8,2)")]
        public decimal DeliveryScore { get; set; }

        /// <summary>評核期間內有沒有到料事實，即交期分數算不算得出來。語意同 <see cref="QualityHasData"/>。</summary>
        public bool DeliveryHasData { get; set; } = true;

        /// <summary>價格分數 0–100（人工填寫；未來可比對市場基準）</summary>
        [Range(0, 100)]
        [Column(TypeName = "decimal(8,2)")]
        public decimal PriceScore { get; set; }

        /// <summary>服務分數 0–100（人工填寫：回應速度、溝通品質等）</summary>
        [Range(0, 100)]
        [Column(TypeName = "decimal(8,2)")]
        public decimal ServiceScore { get; set; }

        /// <summary>總分＝加權平均（權重存系統參數 srm_weight_*）</summary>
        [Column(TypeName = "decimal(8,2)")]
        public decimal TotalScore { get; set; }

        /// <summary>評核人 FK→ApplicationUser</summary>
        [Required, MaxLength(450)]
        public string Evaluator { get; set; } = string.Empty;

        public DateOnly EvaluationDate { get; set; }

        /// <summary>評核結果（由 TotalScore 門檻自動判定）</summary>
        public EvaluationResult ResultStatus { get; set; }

        /// <summary>Fail 時的改善措施（Fail 必填）</summary>
        [MaxLength(500)]
        public string? FollowUpAction { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        // ── 線上簽核（核准後回寫 Company.QualificationStatus）───────────────
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        // ── 稽核欄位 ─────────────────────────────────────────────────────
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public Company? Supplier { get; set; }
    }
}
