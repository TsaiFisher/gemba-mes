namespace SmartFactory.MES.Modules.PUR.ViewModels;

public class OutsourceProgressRowVm
{
    public string OutsourcePurchaseOrderId { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public int LineNo { get; set; }
    public decimal OrderedQty { get; set; }
    public string? UomCode { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal PendingQty => OrderedQty - ReceivedQty;
    public decimal ProgressPct => OrderedQty == 0 ? 0m : Math.Round(ReceivedQty / OrderedQty * 100, 1);
    public bool IsOverdue { get; set; }
    /// <summary>逾期天數（今日 − 預交日；未逾期或無預交日為 0）</summary>
    public int OverdueDays { get; set; }
}
