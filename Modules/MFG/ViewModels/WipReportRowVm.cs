namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class WipReportRowVm
{
    public string WorkOrderId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? UomCode { get; set; }
    public decimal Quantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime PlannedStartDate { get; set; }
    public DateTime PlannedEndDate { get; set; }
    public decimal StdTotalCost { get; set; }
    public decimal ActTotalCost { get; set; }
    public decimal WipValue => ActTotalCost;
    public decimal CostVariance => ActTotalCost - StdTotalCost;
}
