using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers;

[Authorize]
public class InventoryValueController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "InventoryValue";

    public InventoryValueController(
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

    /// <summary>
    /// <paramref name="showDetail"/> 決定畫面的**列單位**，連帶決定分頁切的是什麼：
    /// 關（預設）＝一列一個分類，分頁切分類；開＝一列一個品號，分頁切明細列。
    /// 原本兩者都不是——畫面固定「每個分類一張表、表內列出所有品號」，
    /// 沒有任何一個單一集合可以切，這也是這支之前補不上分頁的原因。
    /// </summary>
    public async Task<IActionResult> Index(
        string? categoryCode, bool showDetail = false, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.CategoryCode  = categoryCode;
        ViewBag.ShowDetail    = showDetail;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);

        // Category options
        ViewBag.Categories = await _context.Products
            .AsNoTracking()
            .Where(p => p.CategoryCode != null)
            .Select(p => p.CategoryCode!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        if (!searched)
        {
            ViewBag.CategoryRows = new List<InventoryValueCategoryVm>();
            return View(new List<InventoryValueRowVm>());
        }

        var rows = await BuildQueryAsync(categoryCode);
        // 分類彙總兩種模式都算：彙總模式拿它當列，明細模式拿它當分類小計。
        // 小計一律取自**完整**結果集，不是當頁——當頁加總會隨翻頁變動，
        // 而那種錯比沒有分頁更難發現（ReportPaging 的註解說的就是這件事）。
        ViewBag.CategoryRows = BuildCategoryRows(rows);
        return View(rows);
    }

    /// <summary>
    /// 匯出跟著畫面的模式走（<paramref name="showDetail"/> 由畫面上的連結帶過來），
    /// 但**不跟著分頁走**——匯出的永遠是完整結果集，不是當頁那 20 列。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export(string? categoryCode, bool showDetail = false)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(categoryCode);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = showDetail
            ? new ExportRequest
            {
                Title   = isZh ? "存貨價值分析" : "Inventory Value Analysis",
                Headers = isZh
                    ? ["品項類別", "品號", "品名", "庫存量", "單位", "移動均價", "庫存金額", "佔比%"]
                    : ["Category", "Product", "Name", "Stock Qty", "UOM", "Avg Cost", "Inventory Value", "Share%"],
                Rows = rows.Select(r => new object?[]
                {
                    r.CategoryCode, r.ProductCode, r.ProductName,
                    r.QtyOnHand, r.StockUom, r.UnitCost, r.TotalValue, r.ValuePct
                }).ToList()
            }
            : new ExportRequest
            {
                Title   = isZh ? "存貨價值分析（分類彙總）" : "Inventory Value by Category",
                Headers = isZh
                    ? ["品項類別", "品號數", "庫存金額", "佔比%"]
                    : ["Category", "Items", "Inventory Value", "Share%"],
                Rows = BuildCategoryRows(rows).Select(c => new object?[]
                {
                    c.CategoryCode, c.ItemCount, c.TotalValue, c.ValuePct
                }).ToList()
            };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"InventoryValue_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 由明細列彙總出分類列。佔比分母用的是**本次查詢結果**的總額，與明細列的
    /// <c>ValuePct</c> 同一個分母，所以分類佔比恆等於其明細佔比之和（四捨五入誤差除外）。
    /// 篩了分類時分母就是那一類，兩邊仍然一致。
    /// </summary>
    private static List<InventoryValueCategoryVm> BuildCategoryRows(List<InventoryValueRowVm> rows)
    {
        var grandTotal = rows.Sum(r => r.TotalValue);
        return rows
            .GroupBy(r => r.CategoryCode)
            .Select(g => new InventoryValueCategoryVm
            {
                CategoryCode = g.Key,
                ItemCount    = g.Count(),
                TotalValue   = g.Sum(r => r.TotalValue),
                ValuePct     = grandTotal == 0
                    ? 0
                    : Math.Round(g.Sum(r => r.TotalValue) / grandTotal * 100, 1),
            })
            .OrderByDescending(c => c.TotalValue)
            .ToList();
    }

    private async Task<List<InventoryValueRowVm>> BuildQueryAsync(string? categoryCode)
    {
        var plantId = _userContext.PlantId ?? "";

        var query =
            from inv in _context.Inventories
            join p in _context.Products on inv.ProductCode equals p.ProductCode
            where inv.PlantId == plantId && inv.QtyOnHand > 0
            select new
            {
                p.CategoryCode,
                inv.ProductCode,
                p.ProductName,
                inv.QtyOnHand,
                p.StockUom,
                inv.UnitCost,
                TotalValue = inv.QtyOnHand * inv.UnitCost,
            };

        if (!string.IsNullOrWhiteSpace(categoryCode))
            query = query.Where(r => r.CategoryCode == categoryCode);

        var raw = await query.AsNoTracking().ToListAsync();

        var grandTotal = raw.Sum(r => r.TotalValue);

        return raw
            .GroupBy(r => new { r.CategoryCode, r.ProductCode, r.ProductName, r.StockUom })
            .Select(g => new InventoryValueRowVm
            {
                CategoryCode = g.Key.CategoryCode,
                ProductCode  = g.Key.ProductCode,
                ProductName  = g.Key.ProductName,
                QtyOnHand    = g.Sum(r => r.QtyOnHand),
                StockUom     = g.Key.StockUom,
                UnitCost     = g.Average(r => r.UnitCost),
                TotalValue   = g.Sum(r => r.TotalValue),
                ValuePct     = grandTotal == 0 ? 0 : Math.Round(g.Sum(r => r.TotalValue) / grandTotal * 100, 1),
            })
            .OrderBy(r => r.CategoryCode)
            .ThenByDescending(r => r.TotalValue)
            .ToList();
    }
}
