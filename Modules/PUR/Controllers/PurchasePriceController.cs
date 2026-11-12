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
public class PurchasePriceController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly ICurrencyService     _currencyService;
    private readonly PurchasePriceTool    _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "PurchasePrice";

    public PurchasePriceController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        ICurrencyService currencyService,
        PurchasePriceTool tool,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _currencyService   = currencyService;
        _tool              = tool;
        _localizer         = localizer;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string? searchProduct, string? searchSupplier,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        ViewBag.FromDate        = fromDate;
        ViewBag.ToDate          = toDate;
        ViewBag.SearchProduct   = searchProduct;
        ViewBag.SearchSupplier  = searchSupplier;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        // 單價一律照該筆交易幣別的小數位數顯示。原本畫面寫死 N4，於是 0 位數的幣別
        // （例如 JPY）也被印成 1,234.0000，而 4 位數以上的幣別反而被截掉。
        ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
        ViewBag.SupplierOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<PurchasePriceRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, searchProduct, searchSupplier);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? searchProduct, string? searchSupplier)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, searchProduct, searchSupplier);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        // 欄頭走工具宣告（§16.1 S-5）：畫面匯出與排程匯出共用同一份。
        var req = _tool.ToExportRequest(
            BuildRequest(fromDate, toDate, searchProduct, searchSupplier), rows,
            isZh ? "採購價格比較" : "Purchase Price Comparison",
            _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"PurchasePrice_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<PurchasePriceRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate, string? searchProduct, string? searchSupplier)
        => (await _tool.ExecuteAsync(
                BuildRequest(fromDate, toDate, searchProduct, searchSupplier), User)).ToList();

    /// <summary>
    /// 畫面參數 → 工具請求。迄日留白時退回今天、起日留白時是 <c>default</c>（0001-01-01），
    /// 兩者都是抽出前的行為，刻意照抄——改預設值是口徑變更，不該夾帶在這一步。
    /// </summary>
    private PurchasePriceRequest BuildRequest(
        string? fromDate, string? toDate, string? searchProduct, string? searchSupplier)
    {
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = DateOnly.FromDateTime(DateTime.Today);

        return new PurchasePriceRequest
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = new DateRange(start, end),
            ProductCode    = searchProduct,
            SupplierCode   = searchSupplier,
        };
    }
}
