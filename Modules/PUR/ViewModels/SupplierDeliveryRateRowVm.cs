namespace SmartFactory.MES.Modules.PUR.ViewModels;

public class SupplierDeliveryRateRowVm
{
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public int TotalLines { get; set; }
    public int OnTimeLines { get; set; }
    public int LateLines => TotalLines - OnTimeLines;
    public decimal OnTimeRate => TotalLines == 0 ? 0m : Math.Round((decimal)OnTimeLines / TotalLines * 100, 1);
    /// <summary>平均逾期天數（僅計逾期筆；無逾期為 0）</summary>
    public decimal AvgOverdueDays { get; set; }
    /// <summary>最大逾期天數</summary>
    public int MaxOverdueDays { get; set; }
}

/// <summary>OTD 月份趨勢列（pur-domain.md §5.3；折線圖資料來源）</summary>
public class OtdMonthRowVm
{
    /// <summary>年月（yyyy-MM）</summary>
    public string YearMonth { get; set; } = string.Empty;
    public int TotalLines { get; set; }
    public int OnTimeLines { get; set; }
    public int LateLines => TotalLines - OnTimeLines;
    public decimal OnTimeRate => TotalLines == 0 ? 0m : Math.Round((decimal)OnTimeLines / TotalLines * 100, 1);
    public decimal AvgOverdueDays { get; set; }
    public int MaxOverdueDays { get; set; }
}

/// <summary>交期達成率頁面模型：月份趨勢＋廠商排名兩個維度（§5.3/§5.5）</summary>
public class SupplierDeliveryRateVm
{
    public List<OtdMonthRowVm> MonthRows { get; set; } = new();
    public List<SupplierDeliveryRateRowVm> SupplierRows { get; set; } = new();
}
