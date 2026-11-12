using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.QCR.Models;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Controllers;

/// <summary>
/// SPC 製程能力分析報表：由檢驗計量值（InspectionItem.MeasuredValue）依（品號 × 特性）
/// 計算 Cp/Cpk/超規率；鎖定單一品號時附時間序管制圖（X-individual + UCL/LCL/規格界限）。
/// </summary>
[Authorize]
public class SpcAnalysisController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly ISpcAnalysisService  _spcService;

    private const string MenuKey = "SpcAnalysis";

    public SpcAnalysisController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        ISpcAnalysisService spcService)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _spcService        = spcService;
    }

    public async Task<IActionResult> Index(
        string? searchProduct, DateOnly? dateFrom, DateOnly? dateTo, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        var (from, to) = ResolveRange(dateFrom, dateTo);
        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.DateFrom      = from;
        ViewBag.DateTo        = to;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new SpcAnalysisReport());

        var report = await _spcService.BuildAsync(
            _userContext.PlantId ?? "", searchProduct, from, to);
        return View(report);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? searchProduct, DateOnly? dateFrom, DateOnly? dateTo)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var (from, to) = ResolveRange(dateFrom, dateTo);
        var report = await _spcService.BuildAsync(
            _userContext.PlantId ?? "", searchProduct, from, to);

        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "SPC 製程能力分析" : "SPC Process Capability",
            Headers = isZh
                ? ["品號", "品名", "檢驗特性", "單位", "樣本數", "平均", "標準差", "最小", "最大",
                   "規格下限", "目標", "規格上限", "Cp", "Cpk", "超規數", "超規率%"]
                : ["Product", "Name", "Characteristic", "Unit", "N", "Mean", "StdDev", "Min", "Max",
                   "LSL", "Target", "USL", "Cp", "Cpk", "OOS", "OOS %"],
            Rows = report.Rows.Select(r => new object?[]
            {
                r.ProductCode, r.ProductName, r.ItemName, r.UomCode,
                r.SampleCount, r.Mean, r.StdDev, r.MinValue, r.MaxValue,
                r.LowerSpec, r.TargetValue, r.UpperSpec, r.Cp, r.Cpk,
                r.OutOfSpecCount, r.OutOfSpecRate,
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SpcAnalysis_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>預設區間：近 90 天。</summary>
    private (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(_dateTimeService.Now);
        var t = to ?? today;
        var f = from ?? t.AddDays(-90);
        if (f > t) (f, t) = (t, f);
        return (f, t);
    }
}
