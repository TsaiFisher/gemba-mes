using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.ViewModels;

/// <summary>
/// 工單進度報表的一列（§六 M1）。
///
/// <para>2026-09-15 由 <c>WorkOrderProgressController</c> 檔內搬到此處，理由同
/// <c>DefectRateRow</c>：工具不該反向相依 Controllers 命名空間。欄位未動。</para>
/// </summary>
public class WorkOrderProgressRow
{
    public string          WorkOrderId      { get; init; } = "";
    public string          ProductCode      { get; init; } = "";
    public string?         UomCode          { get; init; }
    public WorkOrderType   Type             { get; init; }
    public WorkOrderStatus Status           { get; init; }
    public DateTime        PlannedStartDate { get; init; }
    public DateTime        PlannedEndDate   { get; init; }
    public decimal         Quantity         { get; init; }
    public decimal         QtyDone          { get; init; }
    public decimal         ProgressPct      => Quantity == 0 ? 0m
        : Math.Round(QtyDone / Quantity * 100m, 1);
    public bool            IsOverdue        { get; init; }
}
