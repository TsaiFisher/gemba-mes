using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

/// <summary>
/// 多階料表查詢（唯讀）：選一個品號，沿有效途程之工序用料逐階展開，
/// 顯示各階用量/損耗率/累計需求量與來源（自製/採購），可列印與匯出。
/// </summary>
[Authorize]
public class MultiLevelBomController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly IMultiLevelBomService _multiLevelBomService;

    private const string MenuKey = "MultiLevelBom";

    public MultiLevelBomController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IUomService uomService,
        IMultiLevelBomService multiLevelBomService)
    {
        _context              = context;
        _userContext          = userContext;
        _permissionService    = permissionService;
        _exportService        = exportService;
        _dateTimeService      = dateTimeService;
        _uomService           = uomService;
        _multiLevelBomService = multiLevelBomService;
    }

    public async Task<IActionResult> Index(string? searchProduct, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.UomDecimals   = await _uomService.GetAllDecimalPlacesAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched || string.IsNullOrWhiteSpace(searchProduct))
            return View((MultiLevelExplosionResult?)null);

        var result = await ExplodeAsync(searchProduct);
        return View(result);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? searchProduct, string format = "xlsx")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        if (string.IsNullOrWhiteSpace(searchProduct)) return NotFound();

        var result = await ExplodeAsync(searchProduct);
        var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
        var localNow = _dateTimeService.Now;

        var req = new ExportRequest
        {
            Title   = isZh ? "多階料表" : "Multi-Level BOM",
            Headers = isZh
                ? ["階層", "父件品號", "品號", "品名", "單位", "單位用量", "損耗率%", "下角率%", "累計需求量", "來源"]
                : ["Level", "Parent", "Product", "Product Name", "UOM", "Qty Per", "Scrap%", "Offcut%", "Required Qty", "Source"],
            Rows = result.BomRows.Select(r => new object?[]
            {
                r.Level, r.ParentProductCode, r.ProductCode, r.ProductName, r.UomCode,
                r.Quantity, r.ScrapRate, r.OffcutRate, r.RequiredQty,
                r.SourceType == "Make" ? (isZh ? "自製" : "Make") : (isZh ? "採購" : "Buy"),
            }).ToList(),
        };

        return format?.ToLowerInvariant() switch
        {
            "csv"  => File(_exportService.ToCsv(req),  "text/csv",         Uri.EscapeDataString($"MultiLevelBom_{searchProduct}_{localNow:yyyyMMdd}.csv")),
            "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"MultiLevelBom_{searchProduct}_{localNow:yyyyMMdd}.json")),
            _      => File(_exportService.ToExcel(req),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Uri.EscapeDataString($"MultiLevelBom_{searchProduct}_{localNow:yyyyMMdd}.xlsx")),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Print(string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        if (string.IsNullOrWhiteSpace(searchProduct)) return NotFound();

        var result = await ExplodeAsync(searchProduct);
        ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

        var qr = Url.Action("Index", null,
            new { searchProduct, searched = true }, Request.Scheme)!;
        await PreparePrintViewBagAsync(qr);
        return View(result);
    }

    private async Task<MultiLevelExplosionResult> ExplodeAsync(string productCode)
    {
        var today = DateOnly.FromDateTime(_dateTimeService.Now);
        return await _multiLevelBomService.ExplodeAsync(productCode.Trim(), today);
    }
}
