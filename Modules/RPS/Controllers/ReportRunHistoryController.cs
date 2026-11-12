using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPS.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Controllers;

/// <summary>
/// 排程執行紀錄（S-4，§7.4）。唯讀查詢，沒有任何寫入動作。
///
/// <para><b>與收件匣（S-3）的分工</b>：收件匣只列「成功且有檔案」的，因為它的用途是拿報表；
/// 這裡**刻意列出失敗與 <c>SkippedNoPermission</c>**，因為它的用途是回答
/// 「為什麼這個月沒有收到報表」。少了這一頁，§7.8 S2 的「不得靜默停止」
/// 就只是寫在資料庫裡沒人看得到。</para>
/// </summary>
[Authorize]
public class ReportRunHistoryController : ModuleControllerBase
{
    private const string MenuKey = "ReportRunHistory";

    private readonly ApplicationDbContext _context;
    private readonly IPermissionService _permissionService;
    private readonly IDateTimeService _dateTime;

    public ReportRunHistoryController(
        ApplicationDbContext context, IPermissionService permissionService,
        IDateTimeService dateTime)
    {
        _context = context;
        _permissionService = permissionService;
        _dateTime = dateTime;
    }

    // GET: /ReportRunHistory
    public async Task<IActionResult> Index(
        string? status, DateTime? fromDate, DateTime? toDate,
        int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Status = status;
        ViewBag.FromDate = fromDate;
        ViewBag.ToDate = toDate;
        ViewBag.Searched = searched;
        ViewBag.CurrentPageSize = pageSize ?? 20;
        ViewBag.CurrentSort = sortOrder;
        ViewBag.Plants = await ReportPlantDisplay.LoadAsync(_context);
        ViewBag.RunAtSort = sortOrder == "runat_asc" ? "runat_desc" : "runat_asc";

        await PrepareIndexPermissionsAsync(MenuKey);

        if (!searched) return View((PaginatedList<ReportRunHistoryItemVm>?)null);

        // ReportRun 沒有往 ReportDefinition 的 FK（稽核軌跡要能在定義被刪後留存），
        // 所以這裡用 left join——定義刪了仍要看得到那些歷史紀錄。
        var query =
            from run in _context.ReportRuns.AsNoTracking()
            join def in _context.ReportDefinitions.AsNoTracking() on run.DefinitionId equals def.Id into defs
            from def in defs.DefaultIfEmpty()
            select new ReportRunHistoryItemVm
            {
                RunId        = run.RunId,
                DefinitionId = run.DefinitionId,
                Name         = def != null ? def.Name : "",
                ToolKey      = def != null ? def.ToolKey : "",
                PlantId      = def != null ? def.PlantId : null,
                RunAt        = run.RunAt,
                TriggerType  = run.TriggerType,
                Status       = run.Status,
                RowCount     = run.RowCount,
                DurationMs   = run.DurationMs,
                Language     = run.Language,
                ErrorMessage = run.ErrorMessage,
            };

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        // 篩選的日界取**廠別時區的午夜**，不是 UTC 午夜（manual-test-plan §11 #11）。
        // 原本把使用者輸入的日期直接當成 UTC 午夜：UTC+8 廠的使用者在當地上午 8 點前
        // 用「今天」篩，會查不到一小時前才產出的報表——而畫面上沒有任何訊號說明原因。
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
        return View(await PaginatedList<ReportRunHistoryItemVm>.CreateAsync(query, pageNumber ?? 1, ps));
    }
}
