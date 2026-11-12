namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class ProductionEfficiencyRowVm
{
    public string WorkCenterId { get; set; } = string.Empty;
    public int DispatchCount { get; set; }
    public decimal StandardMinutes { get; set; }
    /// <summary>實際工時：各派工上工時段聯集的分鐘合計（不是排程起訖）</summary>
    public decimal ActualMinutes { get; set; }
    public decimal EfficiencyRate => ActualMinutes > 0
        ? Math.Round(StandardMinutes / ActualMinutes * 100, 1) : 0m;
}
