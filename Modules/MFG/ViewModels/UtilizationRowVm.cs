namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class UtilizationRowVm
{
    public string WorkCenterId { get; set; } = string.Empty;

    /// <summary>
    /// 可用工時＝查詢期間內該工作中心的工作日曆工時加總（TM_WKS 正班＋TM_WKO 加班）。
    ///
    /// <para>2026-09-21 之前這裡放的是**查詢期間的日曆總時數**（一個月＝720 小時），
    /// 而不是排得出班的工時（22 天 × 8 小時＝176 小時）——稼動率因此被系統性低估約四倍，
    /// 且期間留白時分母會膨脹到一千七百萬小時、稼動率恆為 0%。</para>
    /// </summary>
    public decimal PlannedHours { get; set; }

    /// <summary>
    /// 該工作中心在查詢期間內有沒有任何一天查得到工作日曆。
    /// <c>false</c> 時 <see cref="PlannedHours"/> 會是 0、稼動率也是 0——那是「沒設定日曆」
    /// 而不是「完全沒稼動」，畫面必須講出來，否則兩者長得一模一樣。
    /// </summary>
    public bool HasCalendar { get; set; }
    public decimal DowntimeHours { get; set; }
    public decimal AvailableHours => PlannedHours - DowntimeHours;
    public decimal ScheduledHours { get; set; }
    public decimal UtilizationRate => AvailableHours > 0
        ? Math.Round(ScheduledHours / AvailableHours * 100, 1) : 0m;
}
