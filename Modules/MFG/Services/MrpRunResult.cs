using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>MRP 展開的需求訊號（採購／委外／缺料），供後續模組或 UI 提示使用。</summary>
    public record MrpDemandSignal(string ProductCode, decimal Qty, DateTime RequiredDate, string Reason);

    /// <summary>委外送外料（隨委外建議附帶，需發給協力廠商的材料）。</summary>
    public record MrpOutsourceMaterial(string ProductCode, decimal Qty, string UomCode);

    /// <summary>可行性預檢未通過的需求列（mfg-domain.md §11；隨 <see cref="MrpExpansionValidationException"/> 回報）。</summary>
    public record MrpSkippedLine(int LineSeq, string ProductCode, string Error);

    /// <summary>
    /// 委外建議（mfg-domain.md §4.1）：工序委外（自製途程中的單道工序）或整品委外（CD_MRP=S）。
    /// 收回品 = ProductCode；送外料 = Materials（需先在廠內備妥再發給協力廠商）。
    /// </summary>
    public record MrpOutsourceDemand(
        string WorkOrderId, int OperationSeq, string ProductCode, decimal Qty,
        DateTime RequiredDate, string Reason, List<MrpOutsourceMaterial> Materials);

    /// <summary>
    /// 一次 MRP 執行（RunAsync）的彙總結果。展開副產品（採購/委外/缺料訊號、
    /// 預計延遲生產批）集中於此回傳，供 Controller 寫日誌或 UI 呈現。
    /// </summary>
    public class MrpRunResult
    {
        public string MrpRunId { get; set; } = string.Empty;

        public int BatchCount { get; set; }
        public int WorkOrderCount { get; set; }
        public int DispatchCount { get; set; }

        /// <summary>本次建立的生產批號。</summary>
        public List<string> CreatedBatchIds { get; } = new();

        /// <summary>倒推防呆判定為「預計延遲」的生產批（mfg-domain.md §5.1）。</summary>
        public List<string> PredictedLateBatchIds { get; } = new();

        /// <summary>需觸發採購的需求（CD_MRP=P；本切片僅記錄，PUR 串接於後續切片）。</summary>
        public List<MrpDemandSignal> PurchaseDemands { get; } = new();

        /// <summary>需觸發委外的建議（工序委外 RoutingOperation.IsOutsource 或整品委外 CD_MRP=S；mfg-domain.md §4.1）。</summary>
        public List<MrpOutsourceDemand> OutsourceDemands { get; } = new();

        /// <summary>僅庫存供應（CD_MRP=Q）但庫存不足的缺料訊號。</summary>
        public List<MrpDemandSignal> Shortages { get; } = new();

        /// <summary>展開過程的警示（查無途程、查無品號等）。</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>可行性預檢未通過、被跳過未展開的需求列（mfg-domain.md §11 Phase B 落地）。</summary>
        public List<MrpSkippedLine> SkippedLines { get; } = new();

        /// <summary>由本次採購需求自動建立的請購單號（供「請購轉採購」消化；無採購需求時為 null）。</summary>
        public string? CreatedPurchaseRequestId { get; set; }

        /// <summary>
        /// 重跑對帳差異（§10-5）：本次展開的採購需求與既有同源請購單明細逐品號比對的結果。
        /// 首次展開或既有請購單仍為草稿（直接覆寫重建）時為空。
        /// </summary>
        public List<MrpPurchaseVariance> PurchaseVariances { get; } = new();
    }

    /// <summary>
    /// MRP 展開可行性預檢未通過（全有全無）：任一需求列有阻擋性錯誤即整張中止、
    /// 不產生任何生產批/工單。呼叫端應回滾交易，並可用 <see cref="FailedLines"/>
    /// 回填各列 ValidationError 供畫面標示。
    /// </summary>
    public class MrpExpansionValidationException : InvalidOperationException
    {
        public IReadOnlyList<MrpSkippedLine> FailedLines { get; }

        public MrpExpansionValidationException(string mrpRunId, IReadOnlyList<MrpSkippedLine> failedLines)
            : base(PzText.Get("Mfg_MrpAbortedFmt", mrpRunId, failedLines.Count) + "\n"
                   + string.Join("\n", failedLines.Select(f => PzText.Get("Mfg_MrpAbortedLineFmt", f.LineSeq, f.ProductCode, f.Error))))
        {
            FailedLines = failedLines;
        }
    }

    /// <summary>BOM 巢狀展開超過防呆上限時拋出（mfg-domain.md §10-3，預設 20 層）。</summary>
    public class MrpBomDepthExceededException : InvalidOperationException
    {
        public MrpBomDepthExceededException(string productCode, int maxDepth)
            : base(PzText.Get("Mfg_BomDepthExceededFmt", maxDepth, productCode)) { }
    }

    /// <summary>用料展開時偵測到循環（品號出現在自身的祖先鏈中，mfg-domain.md §3.1(6)）。</summary>
    public class MrpBomCycleException : InvalidOperationException
    {
        public MrpBomCycleException(string productCode)
            : base(PzText.Get("Mfg_BomCycleStoppedFmt", productCode)) { }
    }

    /// <summary>
    /// MRP 重跑被擋下（mfg-domain.md §10-5 / §12.2）：執行單已有工單投產，不可覆蓋。
    /// 錯誤碼 <see cref="Code"/> = WO_IN_PROGRESS。
    /// </summary>
    public class MrpReRunBlockedException : InvalidOperationException
    {
        public const string Code = "WO_IN_PROGRESS";
        public string WorkOrderId { get; }

        public MrpReRunBlockedException(string workOrderId, string status)
            : base(PzText.Get("Mfg_RerunBlockedWoStartedFmt", Code, workOrderId, status))
        {
            WorkOrderId = workOrderId;
        }
    }
}
