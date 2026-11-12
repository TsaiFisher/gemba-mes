using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Controllers;

[Authorize]
public class SalesForecastController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "SalesForecast";

    public SalesForecastController(
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
        string? searchProduct, int forecastMonths = 3, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        ViewBag.SearchProduct   = searchProduct;
        ViewBag.ForecastMonths  = forecastMonths;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<SalesForecastRowVm>());

        var rows = await BuildQueryAsync(searchProduct, forecastMonths);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? searchProduct, int forecastMonths = 3)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(searchProduct, forecastMonths);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "銷售預測" : "Sales Forecast",
            Headers = isZh
                ? ["品號", "年月", "實績金額", "12月均值", "季節係數", "預測值", "是否預測"]
                : ["Product", "Year-Month", "Actual", "12M Avg", "Seasonal Idx", "Forecast Amount", "Forecast"],
            Rows = rows.Select(r => new object?[]
            {
                r.ProductCode, r.YearMonth,
                r.IsForecast ? null : r.ActualAmount,
                r.MovingAvg,
                r.SeasonalIndex,
                r.ForecastAmount,
                r.IsForecast ? (isZh ? "預測" : "Y") : ""
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SalesForecast_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<SalesForecastRowVm>> BuildQueryAsync(
        string? searchProduct, int forecastMonths)
    {
        // 2026-10-27 驗收回報後改寫，原本四個問題：
        //   1. 「12月均值」只平均有銷售的月份（沒賣的月份不在 GroupBy 結果裡），只賣過兩個月的品號均值高估 6 倍；
        //   2. 預測從「最後一個有銷售的月份」往後推，最後一筆在 5 月就去預測早已過去的 6–8 月；
        //   3. 去年同月沒資料時季節係數當 1，預測值＝均值，畫面看不出係數其實是「沒有」；
        //   4. 金額取交易幣 LineAmount，外幣單與本幣混加；駁回的單也算進去。
        // 現在：最近 12 個「完整月份」補零後除以 12；預測從本月起算；銷售史未滿一年不套季節係數；金額取本幣未稅。
        forecastMonths = Math.Clamp(forecastMonths, 1, 12);
        var plantId   = _userContext.PlantId ?? "";
        var thisMonth = new DateOnly(_dateTimeService.Now.Year, _dateTimeService.Now.Month, 1);
        var winStart  = thisMonth.AddMonths(-12);

        var lines =
            from sol in _context.SalesOrderLines
            join so in _context.SalesOrders on sol.SalesOrderId equals so.SalesOrderId
            where so.PlantId == plantId
               && so.ApprovalStatus != DocumentApprovalStatus.Draft
               && so.ApprovalStatus != DocumentApprovalStatus.Rejected
            select new { sol.ProductCode, so.OrderDate, AmountLocal = sol.LineAmount * sol.ExchangeRate };

        if (!string.IsNullOrWhiteSpace(searchProduct))
            lines = lines.Where(r => r.ProductCode == searchProduct);

        var raw = await lines.AsNoTracking()
            .Where(r => r.OrderDate >= winStart && r.OrderDate < thisMonth)
            .ToListAsync();
        var products = raw.Select(r => r.ProductCode).Distinct().ToList();
        // 季節係數要有整整一年的銷售史才有意義：第一筆訂單早於（含）窗口起點才算
        var firstSale = await lines.AsNoTracking()
            .Where(r => products.Contains(r.ProductCode))
            .GroupBy(r => r.ProductCode)
            .Select(g => new { ProductCode = g.Key, First = g.Min(r => r.OrderDate) })
            .ToDictionaryAsync(x => x.ProductCode, x => x.First);

        var result = new List<SalesForecastRowVm>();
        foreach (var pc in products.OrderBy(x => x))
        {
            var byMonth = raw.Where(r => r.ProductCode == pc)
                .GroupBy(r => new DateOnly(r.OrderDate.Year, r.OrderDate.Month, 1))
                .ToDictionary(g => g.Key, g => g.Sum(r => r.AmountLocal));
            var months = Enumerable.Range(0, 12).Select(i => winStart.AddMonths(i)).ToList();
            decimal Amount(DateOnly m) => byMonth.TryGetValue(m, out var a) ? a : 0m;

            var avg12       = Math.Round(months.Sum(Amount) / 12m, 4);
            var hasSeasonal = firstSale.TryGetValue(pc, out var first) && first < winStart.AddMonths(1);

            foreach (var m in months)
            {
                result.Add(new SalesForecastRowVm
                {
                    ProductCode     = pc,
                    Year            = m.Year,
                    Month           = m.Month,
                    ActualAmount    = Amount(m),
                    MovingAvg       = avg12,
                    SeasonalIndex   = hasSeasonal && avg12 > 0 ? Math.Round(Amount(m) / avg12, 2) : null,
                    IsForecast      = false,
                });
            }

            for (int i = 0; i < forecastMonths; i++)
            {
                var f  = thisMonth.AddMonths(i);
                var si = hasSeasonal && avg12 > 0 ? Math.Round(Amount(f.AddMonths(-12)) / avg12, 2) : (decimal?)null;
                result.Add(new SalesForecastRowVm
                {
                    ProductCode     = pc,
                    Year            = f.Year,
                    Month           = f.Month,
                    ActualAmount    = 0,
                    MovingAvg       = avg12,
                    SeasonalIndex   = si,
                    ForecastAmount  = Math.Round(avg12 * (si ?? 1m), 2),
                    IsForecast      = true,
                });
            }
        }

        return result;
    }
}
