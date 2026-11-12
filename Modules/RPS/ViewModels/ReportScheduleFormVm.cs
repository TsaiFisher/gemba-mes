using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;

namespace SmartFactory.MES.Modules.RPS.ViewModels;

/// <summary>
/// 排程維護表單（S-1）。
///
/// <para><b>為什麼是 VM 而不是直接繫結 <see cref="ReportDefinition"/></b>（§16.2-9）：
/// <c>NextRunAt</c>、<c>OwnerUserId</c>、<c>CreatedAt</c> 這些欄位由伺服器產生，表單不會送。
/// 直接繫結實體的話 <c>ModelState</c> 會因為它們而恆為 invalid，
/// 症狀是**按了存檔沒有任何反應**——不是錯誤訊息，是什麼都沒發生。</para>
/// </summary>
public class ReportScheduleFormVm
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = "";

    /// <summary>對應 <c>IReportTool.ToolKey</c>，同時是查權限用的 MenuKey。</summary>
    [Required]
    [StringLength(100)]
    public string ToolKey { get; set; } = "";

    /// <summary>排程週期。S-1 只建排程，所以這裡**不可為 null**（null 是常用查詢，走 S-2）。</summary>
    public ReportScheduleKind ScheduleKind { get; set; } = ReportScheduleKind.Monthly;

    /// <summary>
    /// 報表的查詢期間，**存滾動視窗不存絕對日期**（§5.2.1）：存成 2026-07-01~07-31 的
    /// 「上月銷售排行」，下個月寄出來的還是七月那一份。
    ///
    /// <para><c>null</c> 只對<b>不吃期間</b>的報表合法（<c>IReportToolDescriptor.RequiresPeriod</c>
    /// 為 <c>false</c>，如庫存評估表看的是當下快照）。吃期間的報表少了它，
    /// 排程執行時 <c>RelativePeriodResolver</c> 會直接拋例外——
    /// 2026-09-17 之前這一欄根本不存在，<c>ParamsJson</c> 一律寫死 <c>{}</c>，
    /// 於是十三支吃期間的報表排程出去全部失敗，四支不吃期間的安靜產出零筆。</para>
    /// </summary>
    public RelativePeriod? Period { get; set; }

    [Range(0, 23)]
    public int ScheduleHour { get; set; } = 8;

    [Range(0, 59)]
    public int ScheduleMinute { get; set; }

    /// <summary>0＝週日。僅 <see cref="ReportScheduleKind.Weekly"/> 使用。</summary>
    [Range(0, 6)]
    public int ScheduleDayOfWeek { get; set; } = 1;

    /// <summary>
    /// 1–31，僅 <see cref="ReportScheduleKind.Monthly"/> 使用。
    /// 選 29–31 時，沒有那一天的月份會**取當月最後一日**（不是跳過該月）——
    /// 畫面上以提示文字說明，否則使用者會以為二月不會執行。
    /// </summary>
    [Range(1, 31)]
    public int ScheduleDayOfMonth { get; set; } = 1;

    /// <summary>Excel／Csv。Email 不實作（§7.3 定案 E1 站內收件匣）。</summary>
    [Required]
    [StringLength(20)]
    public string OutputFormat { get; set; } = "Excel";

    public bool IsActive { get; set; } = true;

    /// <summary>收件人的使用者 Id。空集合＝只有 owner 自己在收件匣看得到。</summary>
    public List<string> RecipientUserIds { get; set; } = [];

    /// <summary>唯讀回顯，讓維護者知道下一次什麼時候跑。</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>唯讀回顯，權限失效停用時要看得出是誰的排程。</summary>
    public string? OwnerUserName { get; set; }

    /// <summary>唯讀顯示：對象廠別（代碼＋名稱）。表單不送，伺服器每次重填。</summary>
    public string? PlantLabel { get; set; }

    /// <summary>唯讀顯示：排程所屬時區（期間與執行時刻依此換算）。</summary>
    public string? TimeZoneId { get; set; }
}
