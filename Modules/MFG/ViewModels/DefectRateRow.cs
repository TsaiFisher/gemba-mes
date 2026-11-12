namespace SmartFactory.MES.Modules.MFG.ViewModels;

/// <summary>
/// 不良率報表的一列（§六 M2）。
///
/// <para>2026-09-15 由 <c>DefectRateController</c> 檔內搬到此處。理由與
/// <c>InventoryValuationRow</c> 當初搬家時相同：抽 <c>IReportTool</c> 之後，
/// 工具會需要這個型別，而工具**不該反向相依 Controllers 命名空間**。
/// 搬的是位置，欄位一個字沒動（R5b 以型別與屬性名為準，不是檔案路徑）。</para>
/// </summary>
public class DefectRateRow
{
    public string  ProductCode  { get; init; } = "";
    public string? UomCode      { get; init; }
    public decimal QtyOk        { get; init; }
    public decimal QtyNg        { get; init; }
    public decimal TotalQty     => QtyOk + QtyNg;
    public decimal DefectRate   => TotalQty == 0 ? 0m
        : Math.Round(QtyNg / TotalQty * 100m, 2);
    public int     ReportCount  { get; init; }
}
