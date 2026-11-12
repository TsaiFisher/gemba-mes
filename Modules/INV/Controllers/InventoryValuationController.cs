using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    [Authorize]
    public class InventoryValuationController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly InventoryValuationTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "InventoryValuation";

        public InventoryValuationController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IExportService exportService,
            IDateTimeService dateTimeService, InventoryValuationTool tool,
            IStringLocalizer<ProjectZReportHeaderResource> headers)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _exportService = exportService;
            _dateTimeService = dateTimeService; _tool = tool;
            _headers = headers;
        }

        public async Task<IActionResult> Index(
            string? searchProduct, string? searchWarehouse, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.ProductSort     = sortOrder == "product_asc" ? "product_desc" : "product_asc";
            ViewBag.ValueSort       = sortOrder == "value_asc"   ? "value_desc"   : "value_asc";
            ViewBag.SearchProduct   = searchProduct;
            ViewBag.SearchWarehouse = searchWarehouse;
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive)
                .OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName })
                .ToListAsync();
            ViewBag.ProductOptions  = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            if (!searched) return View((PaginatedList<InventoryValuationRow>?)null);

            // 查詢本體已抽至 InventoryValuationTool；Index 與 Export 原本各有一份重複的
            // 查詢建構，抽出後只剩工具內的一份。
            var request = BuildRequest(searchProduct, searchWarehouse);
            request.SortBy     = sortOrder;
            request.PageNumber = pageNumber ?? 1;
            request.PageSize   = pageSize ?? 20;

            var result = await _tool.ExecuteAsync(request, User);

            ViewBag.GrandTotal = result.GrandTotal;
            return View(result.Page);
        }

        private InventoryValuationRequest BuildRequest(string? searchProduct, string? searchWarehouse)
            => new()
            {
                PlantId       = _userContext.PlantId,
                Keyword       = searchProduct,
                WarehouseCode = searchWarehouse,
            };

        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchProduct, string? searchWarehouse, string? sortOrder,
            string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            // ⚠️ sortOrder 是簽章既有參數但抽出前就沒被 Export 使用（固定依品號升冪）；
            //    此處維持原行為，不順手「修正」。
            var request = BuildRequest(searchProduct, searchWarehouse);
            var data = await _tool.ExecuteForExportAsync(request);
            var now = _dateTimeService.Now;

            var req = _tool.ToExportRequest(request, data, "InventoryValuation", _headers);

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"InventoryValuation_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"InventoryValuation_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"InventoryValuation_{_userContext.PlantId}_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }
    }
}
