namespace SmartFactory.MES.Modules.INV.ViewModels;

/// <summary>
/// 存貨庫齡分析（roadmap-fin-cst-sub.md §5.3）：依呆滯天數分桶彙總在手庫存金額。
/// 一列 = 一個庫齡區間（0–30 / 31–60 / 61–90 / 91–180 / 181–360 / 360+）。
/// </summary>
public class InventoryAgingRowVm
{
    /// <summary>區間標籤（如 "0–30"、"360+"）</summary>
    public string  BucketLabel    { get; init; } = "";
    /// <summary>區間下界（含）天數，供排序</summary>
    public int     LowerBoundDays { get; init; }
    /// <summary>該區間庫存筆數（品號×儲位×批號）</summary>
    public int     ItemCount      { get; init; }
    /// <summary>該區間庫存金額（Σ QtyOnHand × UnitCost）</summary>
    public decimal InventoryValue { get; init; }
    /// <summary>占總庫存金額比率%（由服務端以總額計算後填入）</summary>
    public decimal ValuePercent   { get; init; }
}
