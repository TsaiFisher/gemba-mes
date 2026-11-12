using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers;

/// <summary>
/// 存貨庫齡分析報表（roadmap-fin-cst-sub.md §5.3）：依呆滯天數分桶彙總在手庫存金額，
/// 評估庫存積壓資金成本。呆滯天數 = 今日 − 該（品號×儲位×批號）最後 StockMovement 日期。
/// 與 SlowMovingInventory（單一門檻清單）互補：本報表提供全區間金額分布。
/// </summary>
[Authorize]
public class InventoryAgingController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "InventoryAging";

    /// <summary>庫存掃描安全上限：庫齡須全載廠別在手庫存後於記憶體計算，超過即拒絕並請以品號縮小範圍。</summary>
    private const int MaxInventoryScan = 20000;

    public InventoryAgingController(
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

    public async Task<IActionResult> Index(string? searchProduct, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<InventoryAgingRowVm>());

        var (rows, tooMany) = await BuildQueryAsync(searchProduct);
        ViewBag.TooMany   = tooMany;
        ViewBag.ScanLimit = MaxInventoryScan;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (rows, tooMany) = await BuildQueryAsync(searchProduct);
        if (tooMany)
            return RedirectToAction(nameof(Index), new { searchProduct, searched = true });

        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "存貨庫齡分析" : "Inventory Aging Analysis",
            Headers = isZh
                ? ["庫齡區間(天)", "庫存筆數", "庫存金額", "占比%"]
                : ["Age Bucket (days)", "Item Count", "Inventory Value", "Value %"],
            Rows = rows.Select(r => new object?[]
                { r.BucketLabel, r.ItemCount, r.InventoryValue, r.ValuePercent }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"InventoryAging_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<(List<InventoryAgingRowVm> Rows, bool TooMany)> BuildQueryAsync(string? searchProduct)
    {
        var plantId = _userContext.PlantId ?? "";
        var today   = _dateTimeService.GetLocalDate();

        var invQuery = _context.Inventories
            .Where(i => i.PlantId == plantId && i.QtyOnHand > 0);
        if (!string.IsNullOrWhiteSpace(searchProduct))
            invQuery = invQuery.Where(i => i.ProductCode.Contains(searchProduct));

        // 安全守門：呆滯天數須記憶體後算，無法在 SQL 端安全截斷。
        if (await invQuery.CountAsync() > MaxInventoryScan)
            return ([], true);

        var inventories = await invQuery.AsNoTracking().ToListAsync();
        if (inventories.Count == 0)
            return (InventoryAgingCalculator.Bucketize(Array.Empty<(int, decimal)>()), false);

        var productCodes = inventories.Select(i => i.ProductCode).Distinct().ToList();

        // 每個（品號, 儲位, 批號）的最後異動日
        var lastMovements = await _context.StockMovements
            .Where(m => m.PlantId == plantId && productCodes.Contains(m.ProductCode))
            .GroupBy(m => new { m.ProductCode, m.LocationCode, m.LotNo })
            .Select(g => new { g.Key.ProductCode, g.Key.LocationCode, g.Key.LotNo, LastDate = g.Max(x => x.MovementDate) })
            .AsNoTracking()
            .ToListAsync();
        var movementMap = lastMovements.ToDictionary(
            m => (m.ProductCode, m.LocationCode, m.LotNo), m => m.LastDate);

        var items = inventories.Select(inv =>
        {
            var idle = movementMap.TryGetValue((inv.ProductCode, inv.LocationCode, inv.LotNo), out var last)
                ? today.DayNumber - last.DayNumber
                : 9999;   // 從未異動 → 歸入 360+
            return (IdleDays: idle, Value: inv.QtyOnHand * inv.UnitCost);
        });

        return (InventoryAgingCalculator.Bucketize(items), false);
    }
}
