using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

/// <summary>
/// 多階途程查詢（唯讀，含料表）：選一個品號，逐階列出各自製件的途程工序
/// （工作中心/整備/單位工時/良率/委外）與每道工序的用料，可列印與匯出。
/// </summary>
[Authorize]
public class MultiLevelRoutingController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly IMultiLevelBomService _multiLevelBomService;

    private const string MenuKey = "MultiLevelRouting";

    public MultiLevelRoutingController(
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
        ViewBag.WorkCenterNames = await LoadWorkCenterNamesAsync(result);
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

        string TimeUnitLabel(OperationTimeUnit u) => u switch
        {
            OperationTimeUnit.Second => isZh ? "秒" : "sec",
            OperationTimeUnit.Hour   => isZh ? "時" : "hr",
            _                        => isZh ? "分" : "min",
        };

        // 攤平：每列一道工序；工序用料以「品號×用量」串接於同列（Excel 檢視友善）
        var rows = new List<object?[]>();
        foreach (var node in result.RoutingNodes)
        {
            foreach (var op in node.Operations)
            {
                var mats = string.Join("; ", op.Boms.Select(b =>
                {
                    var tags = new List<string>();
                    if (b.ScrapRate > 0) tags.Add((isZh ? "損耗" : "scrap") + $" +{b.ScrapRate:0.##}%");
                    if (b.OffcutRate > 0) tags.Add((isZh ? "下角" : "offcut") + $" {b.OffcutRate:0.##}%");
                    var suffix = tags.Count > 0 ? $" ({string.Join(isZh ? "，" : ", ", tags)})" : "";
                    return $"{b.ProductCode} x{b.Quantity:0.####}{suffix}";
                }));
                rows.Add(new object?[]
                {
                    node.Level, node.ProductCode, node.ProductName, node.RoutingId,
                    op.OperationSeq, op.OperationName, op.WorkCenterId,
                    op.SetupTime, TimeUnitLabel(op.SetupTimeUnit), op.UnitTime, TimeUnitLabel(op.TimeUnit),
                    op.YieldRate,
                    op.IsOutsource ? (isZh ? "是" : "Y") : "",
                    mats,
                });
            }
        }

        var req = new ExportRequest
        {
            Title   = isZh ? "多階途程" : "Multi-Level Routing",
            Headers = isZh
                ? ["階層", "品號", "品名", "途程", "工序", "工序名稱", "工作中心", "整備時間", "整備單位", "單位工時", "單位工時單位", "良率%", "委外", "工序用料"]
                : ["Level", "Product", "Product Name", "Routing", "Op Seq", "Operation", "Work Center", "Setup", "Setup Unit", "Unit Time", "Unit Time Unit", "Yield%", "Outsource", "Materials"],
            Rows = rows,
        };

        return format?.ToLowerInvariant() switch
        {
            "csv"  => File(_exportService.ToCsv(req),  "text/csv",         Uri.EscapeDataString($"MultiLevelRouting_{searchProduct}_{localNow:yyyyMMdd}.csv")),
            "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"MultiLevelRouting_{searchProduct}_{localNow:yyyyMMdd}.json")),
            _      => File(_exportService.ToExcel(req),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Uri.EscapeDataString($"MultiLevelRouting_{searchProduct}_{localNow:yyyyMMdd}.xlsx")),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Print(string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        if (string.IsNullOrWhiteSpace(searchProduct)) return NotFound();

        var result = await ExplodeAsync(searchProduct);
        ViewBag.UomDecimals     = await _uomService.GetAllDecimalPlacesAsync();
        ViewBag.WorkCenterNames = await LoadWorkCenterNamesAsync(result);

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

    /// <summary>工作中心代號 → 名稱（清單顯示名稱，不顯示裸代碼）。</summary>
    private async Task<Dictionary<string, string>> LoadWorkCenterNamesAsync(MultiLevelExplosionResult result)
    {
        var wcIds = result.RoutingNodes
            .SelectMany(n => n.Operations.Select(o => o.WorkCenterId))
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();
        if (wcIds.Count == 0) return new Dictionary<string, string>();

        return await _context.WorkCenters.AsNoTracking()
            .Where(w => wcIds.Contains(w.WorkCenterCode))
            .ToDictionaryAsync(w => w.WorkCenterCode, w => w.WorkCenterName);
    }
}
