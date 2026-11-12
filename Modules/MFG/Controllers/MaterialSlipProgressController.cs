using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

[Authorize]
public class MaterialSlipProgressController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "MaterialSlipProgress";

    public MaterialSlipProgressController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IStringLocalizer<ProjectZSharedResource> localizer)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _localizer         = localizer;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string? statusFilter, string? searchWorkOrder,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        ViewBag.FromDate        = fromDate;
        ViewBag.ToDate          = toDate;
        ViewBag.StatusFilter    = statusFilter;
        ViewBag.SearchWorkOrder = searchWorkOrder;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.WorkOrderOptions = await _context.WorkOrders
            .AsNoTracking()
            .OrderByDescending(wo => wo.CreatedAt)
            .Select(wo => wo.WorkOrderId)
            .ToListAsync();

        if (!searched)
            return View(new List<MaterialSlipProgressRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, statusFilter, searchWorkOrder);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? statusFilter, string? searchWorkOrder)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, statusFilter, searchWorkOrder);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "備料進度追蹤" : "Material Slip Progress",
            Headers = isZh
                ? ["備料單號", "備料單條碼", "類別", "狀態", "日期", "工單號", "明細數", "請領量", "確認量", "滿足率%"]
                : ["Slip ID", "Slip No.", "Category", "Status", "Date", "Work Order", "Lines", "Requested", "Confirmed", "Fulfill%"],
            Rows = rows.Select(r => new object?[]
            {
                r.SlipId, r.SlipNo, r.Category, MfgEnumDisplay.Text(_localizer, r.Status),
                r.SlipDate.ToString("yyyy-MM-dd"), r.SourceDocId,
                r.LineCount, r.TotalRequestedQty, r.TotalConfirmedQty, r.FulfillRate
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"MaterialSlipProgress_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<MaterialSlipProgressRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate,
        string? statusFilter, string? searchWorkOrder)
    {
        var plantId = _userContext.PlantId ?? "";
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = DateOnly.FromDateTime(DateTime.Today);

        // 生產發料單：廠內發料（MI）＋重工發料（RI）。重工工單投產自動開的 Push 發料單是 RI（2026-10-15 起），
        // 只看 MI 的話重工工單的發料進度整個不見。
        var query =
            from ms in _context.MaterialSlips
            where ms.PlantId == plantId
               && (ms.Category == MaterialSlipCategory.InternalIssue || ms.Category == MaterialSlipCategory.ReworkIssue)
               && ms.SlipDate >= start
               && ms.SlipDate <= end
            select ms;

        if (!string.IsNullOrWhiteSpace(searchWorkOrder))
            query = query.Where(r => r.SourceDocId == searchWorkOrder);

        if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<MaterialSlipStatus>(statusFilter, out var parsedStatus))
            query = query.Where(r => r.Status == parsedStatus);

        var slips = await query.AsNoTracking().ToListAsync();
        var slipIds = slips.Select(s => s.SlipId).ToList();

        var lineAgg = await _context.MaterialSlipLines
            .AsNoTracking()
            .Where(l => slipIds.Contains(l.SlipId))
            .GroupBy(l => l.SlipId)
            .Select(g => new
            {
                SlipId            = g.Key,
                LineCount         = g.Count(),
                TotalRequestedQty = g.Sum(l => l.RequestedQty),
                TotalConfirmedQty = g.Sum(l => l.ConfirmedQty),
            })
            .ToDictionaryAsync(x => x.SlipId);

        return slips
            .Select(ms =>
            {
                lineAgg.TryGetValue(ms.SlipId, out var agg);
                return new MaterialSlipProgressRowVm
                {
                    SlipId            = ms.SlipId,
                    SlipNo            = ms.SlipNo,
                    Category          = ms.Category.ToString(),
                    Status            = ms.Status,
                    SlipDate          = ms.SlipDate,
                    SourceDocId       = ms.SourceDocId,
                    LineCount         = agg?.LineCount ?? 0,
                    TotalRequestedQty = agg?.TotalRequestedQty ?? 0,
                    TotalConfirmedQty = agg?.TotalConfirmedQty ?? 0,
                };
            })
            .OrderBy(r => r.SlipDate)
            .ThenBy(r => r.SlipId)
            .ToList();
    }
}
