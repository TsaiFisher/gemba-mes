using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.MFG.ViewModels;

/// <summary>生產批投產清單列（mfg-domain.md §24.1）：待投產生產批與其可投產工單數。</summary>
public class BatchReleaseRowVm
{
    public string BatchId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? UomCode { get; set; }
    public decimal Quantity { get; set; }
    public int Priority { get; set; }
    public ProductionBatchStatus Status { get; set; }
    public DateTime PlannedEndDate { get; set; }
    public bool IsPredictedLate { get; set; }
    public int PlannedWorkOrderCount { get; set; }   // 批內仍為 Planned（可投產）的工單數
}

/// <summary>投產確認面板中單一工單的試算列（net-at-release 重算量 + 料況閘口）。</summary>
public class BatchReleasePreviewRowVm
{
    public string WorkOrderId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? UomCode { get; set; }
    public bool IsSemi { get; set; }              // 半成品（有父工單）
    public decimal PlannedQty { get; set; }       // 暫定量
    public decimal RecalculatedQty { get; set; }  // 重算量
    public WorkOrderReleaseOutcome Outcome { get; set; }
    public bool MaterialRisk { get; set; }        // 在途已鎖、貨未到（§24.7）
    public List<string> ShortageProducts { get; set; } = new();
}

