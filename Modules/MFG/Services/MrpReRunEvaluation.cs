using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 重跑可行性等級（mfg-domain.md §10-5 分級閘門）。判準是「下游是否已產生不可回收的
    /// 外部承諾或實體／財務足跡」，不是「單據是否已核准」——已核准但未被消化的請購單，
    /// 以結案（IsClosed）退場的成本為零。
    /// </summary>
    public enum MrpReRunLevel
    {
        /// <summary>L1：下游零消化，重跑可完全收斂。</summary>
        Clean = 0,

        /// <summary>L2：重跑可執行（工單重建），但下游有殘留項需人工處理。</summary>
        Residual = 1,

        /// <summary>L3：已有實體／財務足跡或 Restrict FK 引用，重跑不可執行。</summary>
        Blocked = 2
    }

    /// <summary>重跑阻擋／殘留項的類別。</summary>
    public enum MrpReRunImpedimentKind
    {
        /// <summary>工單已投產（InProgress/Completed/Closed）。</summary>
        WorkOrderStarted = 0,

        /// <summary>派工已回報產量（QtyDone &gt; 0），已有實體產出。</summary>
        DispatchReported = 1,

        /// <summary>派工已有上工／整備紀錄（Restrict FK）。</summary>
        DispatchTouched = 2,

        /// <summary>委外需求已產生但尚未轉委外採購單。</summary>
        OutsourceDemandPending = 3,

        /// <summary>委外需求已轉委外採購單（對外承諾）。</summary>
        OutsourceDemandTransferred = 4,

        /// <summary>請購單送審中，需先撤回才能異動。</summary>
        PurchaseRequestPendingApproval = 5,

        /// <summary>請購單已核准但未轉採購，可用結案退場。</summary>
        PurchaseRequestApproved = 6,

        /// <summary>請購明細已轉採購（對外承諾）。</summary>
        PurchaseRequestOrdered = 7
    }

    /// <summary>
    /// 單一阻擋／殘留項。唯讀評估投影，非表單綁定欄位，故值型別屬性以 [Display] 標註，
    /// 排除於驗證欄位 key 慣例（R5b）外。
    /// </summary>
    public class MrpReRunImpediment
    {
        [Display(Name = "Impediment Kind")]
        public MrpReRunImpedimentKind Kind { get; set; }

        /// <summary>本項對整體等級的貢獻（取所有項的最大值為整體等級）。</summary>
        [Display(Name = "Impediment Level")]
        public MrpReRunLevel Level { get; set; }

        /// <summary>關聯單號（工單／派工／委外需求／請購單）。</summary>
        public string DocumentId { get; set; } = string.Empty;

        /// <summary>補充資訊（如工單狀態字串），供組裝例外訊息用。</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>給使用者看的說明。</summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 重跑前置可行性評估結果。把原本散在 <c>ReRunAsync</c>（三處拋例外）與
    /// <c>MrpRunController.CreatePurchaseRequestFromDemandsAsync</c>（一處僅警示）的四道守門
    /// 收斂成單一前置判定，讓使用者按下重跑之前就知道會發生什麼。
    /// </summary>
    public class MrpReRunEvaluation
    {
        public string MrpRunId { get; set; } = string.Empty;

        /// <summary>整體等級 = 所有阻擋項等級的最大值；無阻擋項時為 <see cref="MrpReRunLevel.Clean"/>。</summary>
        [Display(Name = "ReRun Level")]
        public MrpReRunLevel Level =>
            Impediments.Count == 0 ? MrpReRunLevel.Clean : Impediments.Max(i => i.Level);

        /// <summary>是否可執行重跑（L1/L2 可，L3 不可）。</summary>
        [Display(Name = "Can ReRun")]
        public bool CanReRun => Level != MrpReRunLevel.Blocked;

        public List<MrpReRunImpediment> Impediments { get; } = new();

        /// <summary>阻擋項（L3）——存在任一項即不可重跑。</summary>
        public IEnumerable<MrpReRunImpediment> Blocking =>
            Impediments.Where(i => i.Level == MrpReRunLevel.Blocked);

        /// <summary>殘留項（L2）——重跑可執行，但這些下游單據不會被自動收斂，需人工處理。</summary>
        public IEnumerable<MrpReRunImpediment> Residuals =>
            Impediments.Where(i => i.Level == MrpReRunLevel.Residual);
    }
}
