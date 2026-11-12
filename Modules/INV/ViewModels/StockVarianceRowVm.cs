namespace SmartFactory.MES.Modules.INV.ViewModels;

public class StockVarianceRowVm
{
    public string StockAdjustmentId { get; set; } = string.Empty;
    public DateOnly AdjustDate { get; set; }
    public string? AdjustReasonCode { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public decimal VarianceQty { get; set; }
    public decimal? VarianceAmount { get; set; }
    public string? WhCode { get; set; }
    public string? Remarks { get; set; }
}
