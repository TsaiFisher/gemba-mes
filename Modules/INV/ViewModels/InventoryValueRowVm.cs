namespace SmartFactory.MES.Modules.INV.ViewModels;

public class InventoryValueRowVm
{
    public string? CategoryCode { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal QtyOnHand { get; set; }
    public string? StockUom { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
    public decimal ValuePct { get; set; }
}
