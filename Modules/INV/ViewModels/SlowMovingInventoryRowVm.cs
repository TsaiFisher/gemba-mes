namespace SmartFactory.MES.Modules.INV.ViewModels;

public class SlowMovingInventoryRowVm
{
    public string   ProductCode      { get; init; } = "";
    public string   ProductName      { get; init; } = "";
    public string   LocationCode     { get; init; } = "";
    public string   LotNo            { get; init; } = "";
    public decimal  QtyOnHand        { get; init; }
    public decimal  UnitCost         { get; init; }
    public decimal  InventoryValue   => QtyOnHand * UnitCost;
    public DateOnly? LastMovementDate { get; init; }
    public int      IdleDays         { get; init; }
}
