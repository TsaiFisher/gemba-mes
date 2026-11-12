using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Controllers;

[Authorize]
public class SalesRankingController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly SalesRankingTool     _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "SalesRanking";

    public SalesRankingController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        SalesRankingTool tool,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _tool              = tool;
        _localizer         = localizer;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string groupBy = "Customer",
        bool searched = false)
    {
        // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched  = searched;
        // 期間在本報表只是篩選（不是分母），所以留白給預設近 30 天而不是擋下來。
        // 原本留白＝起日 0001-01-01（掃遍全部歷史）＋迄日「今天」（未來日期的單靜默消失），
        // 兩者畫面上都沒有任何訊號。今日走 IDateTimeService＝廠別當地日，不是伺服器時區（R22）。
        (fromDate, toDate) = ReportDateDefaults.Apply(fromDate, toDate, _dateTimeService.GetLocalDate());

        ViewBag.FromDate  = fromDate;
        ViewBag.ToDate    = toDate;
        ViewBag.GroupBy   = groupBy;
        ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

        if (!searched)
            return View(new List<SalesRankingRowVm>());

        var (_, rows) = await BuildQueryAsync(fromDate, toDate, groupBy);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? fromDate, string? toDate, string groupBy = "Customer")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(fromDate, toDate, groupBy);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var groupLabel = isZh ? (groupBy == "Product" ? "品項" : "客戶") : groupBy;

        var req = _tool.ToExportRequest(request, rows, isZh ? $"銷售排行（依{groupLabel}）" : $"Sales Ranking by {groupLabel}", _headers);
        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SalesRanking_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 查詢邏輯已抽至 <see cref="SalesRankingTool"/>（§5.4），畫面／排程／未來的 LLM 共用同一份。
    /// 這裡只負責把畫面的字串參數轉成強型別請求——字串日期不得滲入工具契約（§5.2.1）。
    /// <see cref="LegacyQueryPeriod"/> 保留既有解析語意（迄日留白＝今天），行為不變。
    /// </summary>
    private async Task<(SalesRankingRequest Request, List<SalesRankingRowVm> Rows)> BuildQueryAsync(string? fromDate, string? toDate, string groupBy)
    {
        var request = new SalesRankingRequest
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = LegacyQueryPeriod.Parse(fromDate, toDate,
                                 // 廠別當地日（R22）。查詢列的起訖已由 ReportDateDefaults 補齊，
                                 // 這裡是解析失敗時的最後防線。
                                 _dateTimeService.GetLocalDate()),
            GroupBy        = groupBy,
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }
}
