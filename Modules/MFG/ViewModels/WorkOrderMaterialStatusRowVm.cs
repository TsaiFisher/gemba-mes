using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.MFG.ViewModels
{
    /// <summary>
    /// 工單用料狀況一列＝一筆 WorkOrderMaterial（工單 × 工序 × BOM 序）。
    /// 需求／發料側取自 WorkOrderMaterial（發料單位），備齊狀態口徑與
    /// <see cref="IWorkOrderMaterialReadinessService"/>（mfg-domain.md §21.5）一致。
    /// </summary>
    public class WorkOrderMaterialStatusRowVm
    {
        public string  ProductionBatchId  { get; set; } = string.Empty;
        public string  WorkOrderId        { get; set; } = string.Empty;
        public WorkOrderStatus Status     { get; set; }
        public string  ParentProductCode  { get; set; } = string.Empty;

        public int     OperationSeq       { get; set; }
        public int     BomSeq             { get; set; }
        public string  ProductCode        { get; set; } = string.Empty;
        public string? ProductName        { get; set; }
        public string  UomCode            { get; set; } = string.Empty;

        public MaterialIssueMethod IssueMethod { get; set; }

        /// <summary>子階自製工單（有值代表本料由下階工單供給）。</summary>
        public string? ChildWorkOrderId   { get; set; }

        public decimal RequiredQty        { get; set; }
        public decimal IssuedQty          { get; set; }
        public decimal ReturnedQty        { get; set; }

        /// <summary>淨發料量＝已發 − 已退。</summary>
        public decimal NetIssuedQty       => IssuedQty - ReturnedQty;

        /// <summary>未發量＝需求 − 淨發（下限 0）。</summary>
        public decimal OpenQty            => Math.Max(RequiredQty - NetIssuedQty, 0m);

        /// <summary>廠級可用量（在手 − Active 在手預留，排除待驗倉）；Backflush／下階自製料不適用時為 0。</summary>
        public decimal AvailableQty       { get; set; }

        /// <summary>備齊狀態（§21.5 口徑）。</summary>
        public MaterialLineReadiness ReadinessState { get; set; }
    }
}
