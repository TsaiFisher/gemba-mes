using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>重跑對帳的差異類型（本次展開的採購需求 vs 既有同源請購單明細）。</summary>
    public enum MrpPurchaseVarianceKind
    {
        /// <summary>本次新增的採購需求，既有請購單沒有這個品號。</summary>
        Added = 0,

        /// <summary>兩邊都有，本次需求量變多。</summary>
        Increased = 1,

        /// <summary>兩邊都有，本次需求量變少。</summary>
        Decreased = 2,

        /// <summary>
        /// 品號產銷模式改變（採購 → 自製／委外）：本次已無採購需求，
        /// 但同一次展開有該品號的工單，需求已移轉到生產通道。
        /// </summary>
        ModeChanged = 3,

        /// <summary>本次已無此採購需求，且無對應工單（需求本身消失，例如訂單縮量）。</summary>
        Removed = 4
    }

    /// <summary>差異項的處置結果。</summary>
    public enum MrpPurchaseVarianceDisposition
    {
        /// <summary>僅為提示，無需處置（如數量變動但單據已核准不可自動改）。</summary>
        Informational = 0,

        /// <summary>已自動結案退場（系統參數 mfg_mrp_auto_close_obsolete_pr 開啟）。</summary>
        AutoClosed = 1,

        /// <summary>可安全結案退場，但未自動執行，待人工確認。</summary>
        PendingClose = 2,

        /// <summary>已有下游消化（已轉採購／送審中），不可自動處置，須人工判斷。</summary>
        ManualRequired = 3
    }

    /// <summary>
    /// 單筆採購對帳差異。唯讀結果投影，非表單綁定欄位，故值型別屬性以 [Display] 標註，
    /// 排除於驗證欄位 key 慣例（R5b）外。
    /// </summary>
    public class MrpPurchaseVariance
    {
        [Display(Name = "Variance Kind")]
        public MrpPurchaseVarianceKind Kind { get; set; }

        [Display(Name = "Variance Disposition")]
        public MrpPurchaseVarianceDisposition Disposition { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        /// <summary>關聯請購單號（Added 類型時可能為空）。</summary>
        public string PurchaseRequestId { get; set; } = string.Empty;

        /// <summary>關聯請購單明細序號（Added 類型時為 0）。</summary>
        [Display(Name = "Variance Line No")]
        public int LineNo { get; set; }

        /// <summary>既有請購單明細的數量。</summary>
        [Display(Name = "Variance Previous Qty")]
        public decimal PreviousQty { get; set; }

        /// <summary>本次展開算出的數量。</summary>
        [Display(Name = "Variance Current Qty")]
        public decimal CurrentQty { get; set; }

        /// <summary>給使用者看的說明（含建議動作）。</summary>
        public string Description { get; set; } = string.Empty;
    }
}
