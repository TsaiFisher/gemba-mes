using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers;

[Authorize]
public class PurchaseAmountController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly PurchaseAmountTool   _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "PurchaseAmount";

    public PurchaseAmountController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        PurchaseAmountTool tool,
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
        int? year, string? groupBy, string? source = null,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        groupBy ??= "Supplier";
        source  ??= SourceAll;
        ViewBag.Searched = searched;
        ViewBag.Year     = year ?? _dateTimeService.Now.Year;
        ViewBag.GroupBy  = groupBy;
        ViewBag.Source   = source;
        ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);

        if (!searched)
            return View(new List<PurchaseAmountRowVm>());

        var rows = await BuildQueryAsync(year, groupBy, source);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(int? year, string? groupBy, string? source = null)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        groupBy ??= "Supplier";
        source  ??= SourceAll;
        var rows = await BuildQueryAsync(year, groupBy, source);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        // 欄頭走工具宣告（§16.1 S-5）：第一欄隨分組維度在「供應商／品號」之間切換，
        // 那個切換也在工具那一份裡，畫面與排程不會各切各的。
        var req = _tool.ToExportRequest(
            BuildRequest(year, groupBy, source), rows,
            isZh ? "採購金額比較" : "Purchase Amount Comparison",
            _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"PurchaseAmount_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<PurchaseAmountRowVm>> BuildQueryAsync(int? year, string groupBy, string source)
        => (await _tool.ExecuteAsync(BuildRequest(year, groupBy, source), User)).ToList();

    /// <summary>
    /// 畫面參數 → 工具請求。畫面給的是絕對年度，所以直接串成該年 1/1–12/31 的區間；
    /// 常用查詢那一側走的是相對期間（ThisYear／LastYear），由
    /// <c>ReportPeriodStyle.Year</c> 解算後回填 <c>year=</c>。
    /// </summary>
    private PurchaseAmountRequest BuildRequest(int? year, string groupBy, string source)
    {
        var refYear = year ?? _dateTimeService.Now.Year;
        return new PurchaseAmountRequest
        {
            PlantId          = _userContext.PlantId,
            AbsolutePeriod   = new DateRange(new DateOnly(refYear, 1, 1), new DateOnly(refYear, 12, 31)),
            GroupByDimension = groupBy,
            Source           = source,
        };
    }

    /// <summary>來源別。與供應商交期達成率（§5.3）用同一組值，兩張表才對得起來。</summary>
    /// <remarks>值的唯一來源是 <see cref="PurchaseAmountTool"/>；這裡只是畫面端的別名，
    /// 讓 View 與既有呼叫端不必改成引用 Reporting 命名空間。</remarks>
    public const string SourceAll = PurchaseAmountTool.SourceAll;
    public const string SourcePo  = PurchaseAmountTool.SourcePo;
    public const string SourceOsp = PurchaseAmountTool.SourceOsp;
}
