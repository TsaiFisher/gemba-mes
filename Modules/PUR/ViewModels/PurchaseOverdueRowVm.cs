namespace SmartFactory.MES.Modules.PUR.ViewModels;

public class PurchaseOverdueRowVm
{
    public string   PurchaseOrderId { get; init; } = "";
    public string   SupplierCode    { get; init; } = "";
    public DateOnly OrderDate       { get; init; }
    public string   ProductCode     { get; init; } = "";
    public int      LineNo          { get; init; }
    public DateOnly DeliveryDate    { get; init; }
    public decimal  OrderQty        { get; init; }
    public decimal  ReceivedQty     { get; init; }
    public decimal  PendingQty      => OrderQty - ReceivedQty;
    public int      OverdueDays     { get; init; }
    /// <summary>採購員（採購單建單人的顯示名）</summary>
    public string   Buyer           { get; init; } = "";
}
