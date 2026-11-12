namespace SmartFactory.MES.Modules.INV.ViewModels;

public class InventoryTurnoverRowVm
{
    public string? CategoryCode { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal OutboundCost { get; set; }
    public decimal InventoryValue { get; set; }
    public decimal TurnoverRate { get; set; }
    public decimal? DaysSalesInventory => TurnoverRate > 0 ? Math.Round(365m / TurnoverRate, 1) : null;
}
