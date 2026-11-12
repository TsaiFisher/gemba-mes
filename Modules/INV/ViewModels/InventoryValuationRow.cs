namespace SmartFactory.MES.Modules.INV.ViewModels;

/// <summary>
/// 存貨價值分析的一列。原本宣告在 <c>InventoryValuationController.cs</c> 檔尾，
/// Phase 0-B 抽 <c>IReportTool</c> 時移到 ViewModels——否則工具會反向相依 Controllers 命名空間。
/// </summary>
public class InventoryValuationRow
{
    public string  ProductCode  { get; set; } = "";
    public string  ProductName  { get; set; } = "";
    public string  LocationCode { get; set; } = "";
    public string  LotNo        { get; set; } = "";
    public decimal QtyOnHand    { get; set; }
    public decimal UnitCost     { get; set; }
    public decimal TotalValue   { get; set; }
}
