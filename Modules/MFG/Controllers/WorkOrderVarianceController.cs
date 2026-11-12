using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    [Authorize]
    public class WorkOrderVarianceController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IWorkOrderCostFinalizationService _costFinalizer;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "WorkOrderVariance";

        public WorkOrderVarianceController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IExportService exportService,
            IDateTimeService dateTimeService,
            IWorkOrderCostFinalizationService costFinalizer,
            IUomService uomService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _exportService = exportService;
            _dateTimeService = dateTimeService;
            _costFinalizer = costFinalizer;
            _uomService = uomService;
            _currencyService = currencyService;
            _localizer = localizer;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        /// <summary>§16.3：手動將指定會計期間（YYYYMM）的 Preliminary 工單成本鎖定為 Final。
        /// 月結第 4 步亦會自動觸發；此入口供結算前單獨鎖定使用。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Finalize(string periodId)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(periodId))
                return Json(new { success = false, message = _localizer["CostFinalizePeriodRequired"].Value });

            var plantId = _userContext.PlantId ?? "";
            try
            {
                var count = await _costFinalizer.FinalizeAsync(periodId, plantId);
                return Json(new { success = true, message = _localizer["CostFinalizeSuccess", count].Value });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> Index(
            string? searchProduct, DateOnly? dateFrom, DateOnly? dateTo, string? sortOrder,
            WorkOrderType? workOrderType = null,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.ProductSort     = sortOrder == "product_asc" ? "product_desc" : "product_asc";
            ViewBag.VarSort         = sortOrder == "var_asc"     ? "var_desc"     : "var_asc";
            ViewBag.DateSort        = sortOrder == "date_asc"    ? "date_desc"    : "date_asc";
            ViewBag.SearchProduct   = searchProduct;
            ViewBag.DateFrom        = dateFrom;
            ViewBag.DateTo          = dateTo;
            ViewBag.WorkOrderType   = workOrderType;
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
            ViewBag.ProductOptions  = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            if (!searched) return View((PaginatedList<WorkOrderVarianceRow>?)null);

            var query = from woc in _context.WorkOrderActualCosts
                        join wo  in _context.WorkOrders on woc.WorkOrderId equals wo.WorkOrderId
                        join p   in _context.Products on wo.ProductCode equals p.ProductCode into pJoin
                        from p in pJoin.DefaultIfEmpty()
                        where woc.PlantId == _userContext.PlantId
                        select new WorkOrderVarianceRow
                        {
                            WorkOrderId      = woc.WorkOrderId,
                            ProductCode      = wo.ProductCode,
                            UomCode          = p != null ? p.StockUom : null,
                            PlanQty          = wo.Quantity,
                            WorkOrderType    = wo.Type,
                            CostDate         = woc.CostDate,
                            StdMaterial      = woc.StandardMaterial,
                            StdLabor         = woc.StandardLabor,
                            StdOverhead      = woc.StandardOverhead,
                            ActualMaterial   = woc.ActualMaterial,
                            ActualLabor      = woc.ActualLabor,
                            ActualOverhead   = woc.ActualOverhead,
                            ActualOutsource  = woc.ActualOutsource,
                            ReworkLoss       = woc.ReworkLoss,
                            VarMaterial      = woc.VarMaterial,
                            VarLabor         = woc.VarLaborEfficiency,
                            VarOverhead      = woc.VarOverheadRate,
                            TotalVariance    = woc.VarMaterial + woc.VarLaborEfficiency + woc.VarOverheadRate,
                            CostStatus       = woc.Status,
                        };

            if (!string.IsNullOrWhiteSpace(searchProduct))
                query = query.Where(r => r.ProductCode.Contains(searchProduct)
                                      || r.WorkOrderId.Contains(searchProduct));
            if (dateFrom.HasValue)
                query = query.Where(r => r.CostDate >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(r => r.CostDate <= dateTo.Value);
            if (workOrderType.HasValue)
                query = query.Where(r => r.WorkOrderType == workOrderType.Value);

            query = sortOrder switch
            {
                "product_asc"  => query.OrderBy(r => r.ProductCode),
                "product_desc" => query.OrderByDescending(r => r.ProductCode),
                "var_asc"      => query.OrderBy(r => r.TotalVariance),
                "var_desc"     => query.OrderByDescending(r => r.TotalVariance),
                "date_desc"    => query.OrderByDescending(r => r.CostDate),
                _              => query.OrderByDescending(r => r.CostDate)
            };

            ViewBag.TotalVarMaterial  = await query.AsNoTracking().SumAsync(r => r.VarMaterial);
            ViewBag.TotalVarLabor     = await query.AsNoTracking().SumAsync(r => r.VarLabor);
            ViewBag.TotalVarOverhead  = await query.AsNoTracking().SumAsync(r => r.VarOverhead);
            ViewBag.TotalVariance     = await query.AsNoTracking().SumAsync(r => r.TotalVariance);

            // 重工成本分開統計（mfg-domain.md §16.4b）：重工工單的實際總成本＝品質異常成本，
            // 其中沒有回到存貨的部分是 5250 重工損失。跟著查詢條件走（含類型篩選）。
            var rework = query.AsNoTracking().Where(r => r.WorkOrderType == WorkOrderType.Rework);
            ViewBag.ReworkCount       = await rework.CountAsync();
            ViewBag.ReworkActualTotal = await rework.SumAsync(r => r.ActualMaterial + r.ActualLabor + r.ActualOverhead + r.ActualOutsource);
            ViewBag.ReworkLossTotal   = await rework.SumAsync(r => r.ReworkLoss);

            var paged = await PaginatedList<WorkOrderVarianceRow>.CreateAsync(
                query.AsNoTracking(), pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchProduct, DateOnly? dateFrom, DateOnly? dateTo, string? sortOrder,
            WorkOrderType? workOrderType = null, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var query = from woc in _context.WorkOrderActualCosts
                        join wo  in _context.WorkOrders on woc.WorkOrderId equals wo.WorkOrderId
                        where woc.PlantId == _userContext.PlantId
                        select new WorkOrderVarianceRow
                        {
                            WorkOrderId    = woc.WorkOrderId,
                            ProductCode    = wo.ProductCode,
                            PlanQty        = wo.Quantity,
                            WorkOrderType  = wo.Type,
                            CostDate       = woc.CostDate,
                            StdMaterial    = woc.StandardMaterial,
                            StdLabor       = woc.StandardLabor,
                            StdOverhead    = woc.StandardOverhead,
                            ActualMaterial = woc.ActualMaterial,
                            ActualLabor    = woc.ActualLabor,
                            ActualOverhead = woc.ActualOverhead,
                            ActualOutsource = woc.ActualOutsource,
                            ReworkLoss     = woc.ReworkLoss,
                            VarMaterial    = woc.VarMaterial,
                            VarLabor       = woc.VarLaborEfficiency,
                            VarOverhead    = woc.VarOverheadRate,
                            TotalVariance  = woc.VarMaterial + woc.VarLaborEfficiency + woc.VarOverheadRate,
                            CostStatus     = woc.Status,
                        };

            if (!string.IsNullOrWhiteSpace(searchProduct))
                query = query.Where(r => r.ProductCode.Contains(searchProduct) || r.WorkOrderId.Contains(searchProduct));
            if (dateFrom.HasValue) query = query.Where(r => r.CostDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(r => r.CostDate <= dateTo.Value);
            if (workOrderType.HasValue) query = query.Where(r => r.WorkOrderType == workOrderType.Value);

            var data = await query.OrderByDescending(r => r.CostDate).AsNoTracking().ToListAsync();
            var isZh = CultureInfo.CurrentUICulture.Name != "en-US";
            var now  = _dateTimeService.Now;

            var req = new ExportRequest
            {
                Title = "WorkOrderVariance",
                Headers = isZh
                    ? ["工單號", "工單類型", "品號", "計畫量", "結算日", "標準料", "實際料", "料差", "標準工", "實際工", "工差", "標準費", "實際費", "費差", "合計差異", "重工損失"]
                    : ["WO", "WOType", "Product", "PlanQty", "CostDate", "StdMat", "ActMat", "VarMat", "StdLab", "ActLab", "VarLab", "StdOvh", "ActOvh", "VarOvh", "TotalVar", "ReworkLoss"],
                Rows = data.Select(r => new object?[]
                { r.WorkOrderId, MfgEnumDisplay.Text(_localizer, r.WorkOrderType), r.ProductCode, r.PlanQty, r.CostDate.ToString("yyyy-MM-dd"),
                  r.StdMaterial, r.ActualMaterial, r.VarMaterial,
                  r.StdLabor, r.ActualLabor, r.VarLabor,
                  r.StdOverhead, r.ActualOverhead, r.VarOverhead,
                  r.TotalVariance, r.ReworkLoss })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"WOVariance_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"WOVariance_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"WOVariance_{_userContext.PlantId}_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }
    }

    public class WorkOrderVarianceRow
    {
        public string WorkOrderId   { get; set; } = "";
        public string ProductCode   { get; set; } = "";
        public string? UomCode      { get; set; }
        public decimal PlanQty      { get; set; }
        public WorkOrderType WorkOrderType { get; set; }
        public DateOnly CostDate    { get; set; }
        public decimal StdMaterial  { get; set; }
        public decimal StdLabor     { get; set; }
        public decimal StdOverhead  { get; set; }
        public decimal ActualMaterial  { get; set; }
        public decimal ActualLabor     { get; set; }
        public decimal ActualOverhead  { get; set; }
        public decimal ActualOutsource { get; set; }
        /// <summary>重工損失（只有重工工單有值，§16.4b）</summary>
        public decimal ReworkLoss      { get; set; }
        public decimal VarMaterial     { get; set; }
        public decimal VarLabor        { get; set; }
        public decimal VarOverhead     { get; set; }
        public decimal TotalVariance   { get; set; }
        public CostStatus CostStatus   { get; set; }
    }
}
