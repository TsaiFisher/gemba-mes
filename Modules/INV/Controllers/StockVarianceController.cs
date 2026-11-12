using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers;

[Authorize]
public class StockVarianceController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "StockVariance";

    public StockVarianceController(
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
        string? searchProduct, string? reasonCode,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.FromDate      = fromDate;
        ViewBag.ToDate        = toDate;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.ReasonCode    = reasonCode;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        // Reason code dropdown from existing adjustments
        ViewBag.ReasonCodes = await _context.StockAdjustments
            .AsNoTracking()
            .Where(a => a.AdjustReasonCode != null)
            .Select(a => a.AdjustReasonCode!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync();

        if (!searched)
            return View(new List<StockVarianceRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, searchProduct, reasonCode);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? searchProduct, string? reasonCode)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, searchProduct, reasonCode);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "盤差分析" : "Stock Variance Analysis",
            Headers = isZh
                ? ["調整單號", "調整日", "調整原因", "品號", "差異量", "差異金額", "倉庫", "備註"]
                : ["Adj. No.", "Adj. Date", "Reason", "Product", "Variance Qty", "Variance Amt", "Warehouse", "Remarks"],
            Rows = rows.Select(r => new object?[]
            {
                r.StockAdjustmentId,
                r.AdjustDate.ToString("yyyy-MM-dd"),
                r.AdjustReasonCode,
                r.ProductCode,
                r.VarianceQty,
                r.VarianceAmount,
                r.WhCode,
                r.Remarks
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"StockVariance_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<StockVarianceRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate,
        string? searchProduct, string? reasonCode)
    {
        var plantId = _userContext.PlantId ?? "";
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = _dateTimeService.GetLocalDate();

        var query =
            from sal in _context.StockAdjustmentLines
            join sa in _context.StockAdjustments on sal.StockAdjustmentId equals sa.StockAdjustmentId
            where sa.PlantId == plantId
               && sa.Status == DocStatus.Posted
               && sa.AdjustDate >= start
               && sa.AdjustDate <= end
            select new
            {
                sa.StockAdjustmentId,
                sa.AdjustDate,
                sa.AdjustReasonCode,
                sal.ProductCode,
                VarianceQty    = sal.StockQty * sal.Direction,
                VarianceAmount = sal.UnitPrice.HasValue ? sal.StockQty * sal.Direction * sal.UnitPrice.Value : (decimal?)null,
                sal.WhCode,
                sal.Remarks
            };

        if (!string.IsNullOrWhiteSpace(searchProduct))
            query = query.Where(r => r.ProductCode == searchProduct);
        if (!string.IsNullOrWhiteSpace(reasonCode))
            query = query.Where(r => r.AdjustReasonCode == reasonCode);

        var raw = await query.AsNoTracking().ToListAsync();

        return raw
            .Select(r => new StockVarianceRowVm
            {
                StockAdjustmentId = r.StockAdjustmentId,
                AdjustDate        = r.AdjustDate,
                AdjustReasonCode  = r.AdjustReasonCode,
                ProductCode       = r.ProductCode,
                VarianceQty       = r.VarianceQty,
                VarianceAmount    = r.VarianceAmount,
                WhCode            = r.WhCode,
                Remarks           = r.Remarks,
            })
            .OrderBy(r => r.AdjustDate)
            .ThenBy(r => r.StockAdjustmentId)
            .ToList();
    }
}
