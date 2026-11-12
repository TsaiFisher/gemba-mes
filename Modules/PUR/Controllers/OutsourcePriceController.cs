using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers;

/// <summary>
/// 委外價格趨勢與比價分析。查詢邏輯全在 <see cref="OutsourcePriceTool"/>——
/// 畫面、排程與未來的 LLM 共用同一份，不在這裡另寫一份 <c>BuildQueryAsync</c>。
/// </summary>
[Authorize]
public class OutsourcePriceController : ModuleControllerBase
{
    private const string MenuKey = "OutsourcePrice";

    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly ICurrencyService     _currencyService;
    private readonly OutsourcePriceTool   _tool;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    public OutsourcePriceController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        ICurrencyService currencyService,
        OutsourcePriceTool tool,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _currencyService   = currencyService;
        _tool              = tool;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        // 留白＝近 30 天，不是「不限起日」。這張報表要逐筆掃收貨明細並逐筆查價，
        // 不給預設值時使用者按下查詢會掃全部歷史。同一慣例見採購／訂單的逾期類報表。
        var (from, to) = ResolveDefaultRange(fromDate, toDate);

        ViewBag.Searched         = searched;
        ViewBag.FromDate         = from.ToString("yyyy-MM-dd");
        ViewBag.ToDate           = to.ToString("yyyy-MM-dd");
        ViewBag.SearchSupplier   = searchSupplier;
        ViewBag.SearchProduct    = searchProduct;
        ViewBag.CanPrint         = await _permissionService.CanPrint(MenuKey);
        ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
        ViewBag.SupplierOptions  = await _context.Companies
            .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched) return View(new OutsourcePriceVm());

        return View(await RunAsync(from, to, searchSupplier, searchProduct));
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate, string? searchSupplier, string? searchProduct,
        string view = "detail")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var (from, to) = ResolveDefaultRange(fromDate, toDate);
        var vm   = await RunAsync(from, to, searchSupplier, searchProduct);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        // 明細欄頭一律走工具宣告（§16.1 S-5）：畫面匯出與排程匯出共用同一份，
        // 各寫一份的話兩份都會是對的、只是不一樣，而沒有任何測試會發現。
        // 月趨勢不在工具的匯出欄位裡（那一份是明細），但欄頭同樣只存在 resx 一處，
        // 不在這裡拼硬編的雙語陣列。
        var req = view == "month"
            ? new ExportRequest
            {
                Title   = isZh ? "委外加工費月趨勢" : "Outsource Price Monthly Trend",
                Headers =
                [
                    _headers["OutsourcePriceTrend.YearMonth"],
                    _headers["OutsourcePriceTrend.ReceiptLines"],
                    _headers["OutsourcePriceTrend.AvgUnitPrice"],
                    _headers["OutsourcePriceTrend.MinUnitPrice"],
                    _headers["OutsourcePriceTrend.MaxUnitPrice"],
                ],
                Rows = vm.MonthRows.Select(m => new object?[]
                {
                    m.YearMonth, m.ReceiptLines,
                    m.AvgUnitPriceLocal, m.MinUnitPriceLocal, m.MaxUnitPriceLocal,
                }).ToList()
            }
            : _tool.ToExportRequest(
                BuildRequest(from, to, searchSupplier, searchProduct),
                vm.Rows,
                isZh ? "委外加工費比價" : "Outsource Price Comparison",
                _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"OutsourcePrice_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private Task<OutsourcePriceVm> RunAsync(
        DateOnly from, DateOnly to, string? supplierCode, string? productCode)
        => _tool.ExecuteAsync(BuildRequest(from, to, supplierCode, productCode), User);

    private OutsourcePriceRequest BuildRequest(
        DateOnly from, DateOnly to, string? supplierCode, string? productCode)
        => new()
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = new DateRange(from, to),
            SupplierCode   = supplierCode,
            ProductCode    = productCode,
        };

    /// <summary>
    /// 期間留白時的預設。刻意回傳具體日期而不是「不限」——並且把解出來的值寫回
    /// <c>ViewBag</c>，讓使用者看得到自己實際查的是哪一段，而不是看到兩個空欄位
    /// 卻拿到一份不知道涵蓋多久的資料。
    /// </summary>
    private (DateOnly From, DateOnly To) ResolveDefaultRange(string? fromDate, string? toDate)
    {
        var today = _dateTimeService.GetLocalDate(null);
        var to    = DateOnly.TryParse(toDate,   out var t) ? t : today;
        var from  = DateOnly.TryParse(fromDate, out var f) ? f : to.AddDays(-30);
        return (from, to);
    }
}
