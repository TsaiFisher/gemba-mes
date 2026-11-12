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
public class InventoryTurnoverController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "InventoryTurnover";

    public InventoryTurnoverController(
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
        string? categoryCode, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched     = searched;
        ViewBag.FromDate     = fromDate;
        ViewBag.ToDate       = toDate;
        ViewBag.CategoryCode = categoryCode;
        ViewBag.CanPrint     = await _permissionService.CanPrint(MenuKey);

        ViewBag.Categories = await _context.Products
            .AsNoTracking()
            .Where(p => p.CategoryCode != null)
            .Select(p => p.CategoryCode!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        if (!searched)
            return View(new List<InventoryTurnoverRowVm>());

        // 週轉率＝「期間」出庫成本 ÷「當下」庫存值：分子隨查詢期間長短變動，分母是即時快照。
        // 起日留白時 DateOnly.TryParse 給 default（0001-01-01），等於拿全部歷史出庫去除
        // 現有庫存——數字會大到沒有意義，而畫面上看起來只是「週轉很快」。故要求明確期間。
        if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
        {
            ViewBag.DateRangeRequired = true;
            return View(new List<InventoryTurnoverRowVm>());
        }

        var rows = await BuildQueryAsync(fromDate, toDate, categoryCode);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate, string? categoryCode)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        // 與 Index 同一道閘，否則直接打 Export 仍匯得出那份分母口徑不一致的報表。
        if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
            return BadRequest(_localizer["DateRangeRequired"].Value);
        var rows = await BuildQueryAsync(fromDate, toDate, categoryCode);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "庫存周轉率" : "Inventory Turnover Rate",
            Headers = isZh
                ? ["品項類別", "品號", "品名", "期間出庫成本", "當前庫存金額", "周轉率", "庫存天數(DSI)"]
                : ["Category", "Product", "Name", "Outbound Cost", "Stock Value", "Turnover Rate", "DSI"],
            Rows = rows.Select(r => new object?[]
            {
                r.CategoryCode, r.ProductCode, r.ProductName,
                r.OutboundCost, r.InventoryValue, r.TurnoverRate, r.DaysSalesInventory
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"InventoryTurnover_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<InventoryTurnoverRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate, string? categoryCode)
    {
        var plantId = _userContext.PlantId ?? "";
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = _dateTimeService.GetLocalDate();

        // 出庫成本（排除調整類）。以「原始方向」計：作廢沖銷列的方向是反轉過的、TotalCost 寫成負數
        // （InventoryPostingService.VoidDocAsync 的慣例），所以
        //   原出庫 ＝ 方向 −1 且金額 ≥ 0；作廢出庫的沖銷 ＝ 方向 +1 且金額 < 0（加總時自然扣回）；
        //   作廢入庫的沖銷 ＝ 方向 −1 且金額 < 0 → 不是出庫，排除。
        // 原本只看方向 −1：已作廢的出庫照算、作廢進貨的沖銷被當成負的出庫。2026-10-22
        var outboundQuery =
            from sm in _context.StockMovements
            where sm.PlantId == plantId
               && ((sm.Direction == -1 && sm.TotalCost >= 0) || (sm.Direction == 1 && sm.TotalCost < 0))
               && sm.MovementDate >= start
               && sm.MovementDate <= end
               && sm.TypeCategory != "Adjustment"
               && sm.TotalCost.HasValue
            group sm by sm.ProductCode into g
            select new { ProductCode = g.Key, OutboundCost = g.Sum(x => x.TotalCost!.Value) };

        var outboundDict = await outboundQuery.AsNoTracking()
            .ToDictionaryAsync(x => x.ProductCode, x => x.OutboundCost);

        // Current inventory value
        var invQuery =
            from inv in _context.Inventories
            join p in _context.Products on inv.ProductCode equals p.ProductCode
            where inv.PlantId == plantId && inv.QtyOnHand > 0
            select new
            {
                p.CategoryCode,
                inv.ProductCode,
                p.ProductName,
                InventoryValue = inv.QtyOnHand * inv.UnitCost,
            };

        if (!string.IsNullOrWhiteSpace(categoryCode))
            invQuery = invQuery.Where(r => r.CategoryCode == categoryCode);

        var invRaw = await invQuery.AsNoTracking().ToListAsync();

        var productNames = invRaw
            .GroupBy(r => r.ProductCode)
            .ToDictionary(g => g.Key, g => (Category: g.First().CategoryCode, Name: g.First().ProductName));

        var invByProduct = invRaw
            .GroupBy(r => r.ProductCode)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.InventoryValue));

        var allProductCodes = productNames.Keys.Union(outboundDict.Keys).ToList();

        return allProductCodes
            .Select(pc =>
            {
                outboundDict.TryGetValue(pc, out var outCost);
                invByProduct.TryGetValue(pc,  out var invVal);
                productNames.TryGetValue(pc,  out var meta);
                var rate = invVal > 0 ? Math.Round(outCost / invVal, 2) : 0m;
                return new InventoryTurnoverRowVm
                {
                    CategoryCode    = meta.Category,
                    ProductCode     = pc,
                    ProductName     = meta.Name ?? "",
                    OutboundCost    = outCost,
                    InventoryValue  = invVal,
                    TurnoverRate    = rate,
                };
            })
            .OrderBy(r => r.CategoryCode)
            .ThenByDescending(r => r.TurnoverRate)
            .ToList();
    }
}
