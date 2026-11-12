using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPS.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Controllers;

/// <summary>
/// 報表收件匣（S-3，§7.3 定案 E1）。唯讀清單＋下載，沒有 Create／Update／Delete。
///
/// <para><b>E1 站內收件匣勝過 Email 附件的關鍵就在這支 controller 的
/// <see cref="Download"/></b>：附件一旦寄出就永久脫離 RBAC——調職、離職後它仍在信箱裡；
/// 而這裡每次下載都會重新檢查一次權限（§7.8 S5）。</para>
/// </summary>
[Authorize]
public class ReportInboxController : ModuleControllerBase
{
    private const string MenuKey = "ReportInbox";

    private readonly ApplicationDbContext _context;
    private readonly IPermissionService _permissionService;
    private readonly IReportSchedulePermissionChecker _schedulePermissions;
    private readonly IReportFileStore _files;
    private readonly IStringLocalizer _localizer;
    private readonly IDateTimeService _dateTime;

    public ReportInboxController(
        ApplicationDbContext context,
        IPermissionService permissionService,
        IReportSchedulePermissionChecker schedulePermissions,
        IReportFileStore files,
        IStringLocalizerFactory localizerFactory,
        IDateTimeService dateTime)
    {
        _context = context;
        _permissionService = permissionService;
        _schedulePermissions = schedulePermissions;
        _files = files;
        _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        _dateTime = dateTime;
    }

    // GET: /ReportInbox
    public async Task<IActionResult> Index(
        DateTime? fromDate, DateTime? toDate,
        int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.FromDate = fromDate;
        ViewBag.ToDate = toDate;
        ViewBag.Searched = searched;
        ViewBag.CurrentPageSize = pageSize ?? 20;
        ViewBag.CurrentSort = sortOrder;
        ViewBag.Plants = await ReportPlantDisplay.LoadAsync(_context);
        ViewBag.RunAtSort = sortOrder == "runat_asc" ? "runat_desc" : "runat_asc";

        await PrepareIndexPermissionsAsync(MenuKey);

        if (!searched) return View((PaginatedList<ReportInboxItemVm>?)null);

        var userId = UserContext.UserId ?? "";

        // 「我的收件匣」＝我是 owner，或我在收件人清單裡。
        // 這一段必須寫在查詢層——撈出來再篩漏一個條件就是越權讀取。
        var myDefinitionIds = _context.ReportDefinitions
            .Where(d => d.OwnerUserId == userId)
            .Select(d => d.Id)
            .Union(_context.ReportRecipients
                .Where(r => r.UserId == userId)
                .Select(r => r.DefinitionId));

        var query =
            from run in _context.ReportRuns.AsNoTracking()
            join def in _context.ReportDefinitions.AsNoTracking() on run.DefinitionId equals def.Id
            where myDefinitionIds.Contains(run.DefinitionId)
               && run.Status == ReportRunStatus.Success
               && run.FilePath != null
            select new ReportInboxItemVm
            {
                RunId        = run.RunId,
                DefinitionId = run.DefinitionId,
                Name         = def.Name,
                ToolKey      = def.ToolKey,
                PlantId      = def.PlantId,
                RunAt        = run.RunAt,
                Language     = run.Language,
                RowCount     = run.RowCount,
                ResolvedFrom = run.ResolvedFrom,
                ResolvedTo   = run.ResolvedTo,
            };

        // 日界取廠別時區的午夜，與收件匣的顯示同一個時區（manual-test-plan §11 #11）。
        if (fromDate.HasValue)
        {
            var start = _dateTime.ToUtcTime(fromDate.Value.Date);
            query = query.Where(r => r.RunAt >= start);
        }

        if (toDate.HasValue)
        {
            var end = _dateTime.ToUtcTime(toDate.Value.Date.AddDays(1));
            query = query.Where(r => r.RunAt < end);
        }

        query = sortOrder == "runat_asc"
            ? query.OrderBy(r => r.RunAt)
            : query.OrderByDescending(r => r.RunAt);

        var ps = pageSize ?? 20;
        return View(await PaginatedList<ReportInboxItemVm>.CreateAsync(query, pageNumber ?? 1, ps));
    }

    /// <summary>
    /// 下載一份已產生的報表（§7.8 S5：**下載時再檢查一次**）。
    ///
    /// <para>三道關卡缺一不可：① 本畫面的讀取權；② 這份報表是不是給我的；
    /// ③ 我**現在**還有沒有那支報表的權限。第三道是重點——產生當下有權限，
    /// 不代表現在還有，而已產生的檔案不會因為調職而自己消失。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Download(long runId)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        var userId = UserContext.UserId ?? "";

        var row = await (
            from run in _context.ReportRuns.AsNoTracking()
            join def in _context.ReportDefinitions.AsNoTracking() on run.DefinitionId equals def.Id
            where run.RunId == runId
            select new { run.FilePath, run.RunAt, def.Name, def.ToolKey, def.OwnerUserId, def.Id })
            .FirstOrDefaultAsync();

        if (row is null || string.IsNullOrWhiteSpace(row.FilePath)) return NotFound();

        // ② 收件人或 owner 才拿得到。不是的話一律 NotFound 而非 Forbid——
        // 分開回報等於提供一個以 runId 探測「這份報表存不存在」的管道。
        var isRecipient = row.OwnerUserId == userId
            || await _context.ReportRecipients.AsNoTracking()
                .AnyAsync(r => r.DefinitionId == row.Id && r.UserId == userId);

        if (!isRecipient) return NotFound();

        // ③ §7.8 S5：現在還有沒有那支報表的權限。
        var check = await _schedulePermissions.CheckRecipientAsync(userId, row.ToolKey);
        if (!check.Granted) return Forbid();

        var bytes = await _files.ReadAsync(row.FilePath);
        if (bytes is null)
        {
            // 過了保留期被清掉是正常狀態，要講清楚而不是丟 500。
            TempData["ErrorMessage"] = _localizer["ReportFileExpired"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        var extension = Path.GetExtension(row.FilePath);
        var contentType = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? "text/csv"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        // 檔名給人看的，用報表名＋執行日；儲存用的 GUID 檔名不必外露。
        var fileName = $"{row.Name}-{row.RunAt:yyyyMMdd}{extension}";
        return File(bytes, contentType, fileName);
    }
}
