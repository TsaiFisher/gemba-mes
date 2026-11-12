using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

[Authorize]
public class WipReportController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly ICurrencyService     _currencyService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IWorkOrderIssuedMaterialCostReader        _issuedMaterial;

    private const string MenuKey = "WipReport";

    public WipReportController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IUomService uomService,
        ICurrencyService currencyService,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IWorkOrderIssuedMaterialCostReader issuedMaterial)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _uomService        = uomService;
        _currencyService   = currencyService;
        _localizer         = localizer;
        _issuedMaterial    = issuedMaterial;
    }

    private async Task<int> ResolveBaseCurrencyDpAsync()
    {
        var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
        if (string.IsNullOrEmpty(baseCurrency)) return 2;
        var map = await _currencyService.GetAllDecimalPlacesAsync();
        return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
    }

    public async Task<IActionResult> Index(
        string? searchProduct, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();
        ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
        ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

        if (!searched)
            return View(new List<WipReportRowVm>());

        var rows = await BuildQueryAsync(searchProduct);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(searchProduct);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "在製品 WIP 報表" : "WIP Report",
            Headers = isZh
                ? ["工單號", "品號", "計劃量", "狀態", "計劃開始", "計劃結束", "標準成本", "實際成本", "WIP金額", "成本差異"]
                : ["Work Order", "Product", "Qty", "Status", "Planned Start", "Planned End", "Std Cost", "Act Cost", "WIP Value", "Variance"],
            Rows = rows.Select(r => new object?[]
            {
                r.WorkOrderId, r.ProductCode, r.Quantity,
                r.Status switch
                {
                    nameof(WorkOrderStatus.Released)   => isZh ? "已釋出" : "Released",
                    nameof(WorkOrderStatus.InProgress) => isZh ? "生產中" : "In Progress",
                    _                                  => r.Status
                },
                r.PlannedStartDate.ToString("yyyy-MM-dd"), r.PlannedEndDate.ToString("yyyy-MM-dd"),
                r.StdTotalCost, r.ActTotalCost, r.WipValue, r.CostVariance
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"WipReport_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<WipReportRowVm>> BuildQueryAsync(string? searchProduct)
    {
        var plantId = _userContext.PlantId ?? "";

        var query =
            from wo in _context.WorkOrders
            where wo.PlantId == plantId
               && (wo.Status == WorkOrderStatus.Released || wo.Status == WorkOrderStatus.InProgress)
            join p in _context.Products on wo.ProductCode equals p.ProductCode into pJoin
            from p in pJoin.DefaultIfEmpty()
            select new
            {
                wo.WorkOrderId,
                wo.ProductCode,
                UomCode = p != null ? p.StockUom : null,
                wo.Quantity,
                StatusName = wo.Status.ToString(),
                wo.PlannedStartDate,
                wo.PlannedEndDate,
                StdTotalCost = wo.StandardMaterialCost + wo.StandardLaborCost + wo.StandardOverheadCost,
                // 材料腿刻意不取 wo.ActualMaterialCost：那個欄位只在完工入庫時回寫
                // （ProductionCompletionService），而本報表只收在製工單，兩者互斥——
                // 直接相加的話，料已離開倉庫、成品還沒生出來的那段期間，
                // WIP 金額恆不含材料（見 W2-F7）。改與月結快照同源，於下方合併。
                ActLaborOhCost = wo.ActualLaborCost + wo.ActualOverheadCost,
            };

        if (!string.IsNullOrWhiteSpace(searchProduct))
            query = query.Where(r => r.ProductCode == searchProduct);

        var raw = await query.AsNoTracking().ToListAsync();

        var materialByWo = await _issuedMaterial.IssuedNetCostByWorkOrderAsync(
            raw.Select(r => r.WorkOrderId).Distinct().ToList());

        return raw
            .Select(r => new WipReportRowVm
            {
                WorkOrderId      = r.WorkOrderId,
                ProductCode      = r.ProductCode,
                UomCode          = r.UomCode,
                Quantity         = r.Quantity,
                Status           = r.StatusName,
                PlannedStartDate = r.PlannedStartDate,
                PlannedEndDate   = r.PlannedEndDate,
                StdTotalCost     = r.StdTotalCost,
                ActTotalCost     = materialByWo.GetValueOrDefault(r.WorkOrderId) + r.ActLaborOhCost,
            })
            .OrderBy(r => r.PlannedEndDate)
            .ThenBy(r => r.WorkOrderId)
            .ToList();
    }
}
