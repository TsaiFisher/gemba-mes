using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// 庫存異動明細查詢（唯讀）。依廠別過濾，支援期間 / 異動類別 / 品號 / 單號 / 儲位 / 批號 篩選。
    /// </summary>
    [Authorize]
    public class StockMovementController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;

        private const string MenuKey = "StockMovement";

        public StockMovementController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IExportService exportService,
            IUomService uomService,
            ICurrencyService currencyService)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _exportService = exportService;
            _uomService = uomService;
            _currencyService = currencyService;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 6;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? Math.Max(dp + 4, 4) : 6;
        }

        public async Task<IActionResult> Index(
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? typeCode = null,
            string? productCode = null,
            string? docNo = null,
            string? locationCode = null,
            string? lotNo = null,
            int? pageNumber = null,
            int? pageSize = null,
            string? sortOrder = null,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.DateFrom        = dateFrom;
            ViewBag.DateTo          = dateTo;
            ViewBag.TypeCode        = typeCode;
            ViewBag.ProductCode     = productCode;
            ViewBag.DocNo           = docNo;
            ViewBag.LocationCode    = locationCode;
            ViewBag.LotNo           = lotNo;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.DateSort        = sortOrder == "date_asc"    ? "date_desc"    : "date_asc";
            ViewBag.DocSort         = sortOrder == "doc_asc"     ? "doc_desc"     : "doc_asc";
            ViewBag.ProductSort     = sortOrder == "product_asc" ? "product_desc" : "product_asc";

            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.TypeOptions = await _context.InventoryTransactionTypes
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.TypeCode)
                .Select(t => new { t.TypeCode, t.TypeName, t.Direction })
                .ToListAsync();

            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.LocationOptions = await _context.StorageLocations
                .ForCurrentPlant(_context)
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.LocationCode, l.LocationName, l.WhCode })
                .ToListAsync();

            if (!searched) return View((PaginatedList<StockMovementRowVm>?)null);

            var plantId = _userContext.PlantId ?? "";
            var rows = await BuildQueryAsync(plantId, dateFrom, dateTo, typeCode, productCode, docNo, locationCode, lotNo);

            IEnumerable<StockMovementRowVm> sorted = sortOrder switch
            {
                "date_asc"     => rows.OrderBy(r => r.MovementDate).ThenBy(r => r.CreatedAt),
                "doc_asc"      => rows.OrderBy(r => r.DocNo).ThenBy(r => r.DocLineNo),
                "doc_desc"     => rows.OrderByDescending(r => r.DocNo).ThenByDescending(r => r.DocLineNo),
                "product_asc"  => rows.OrderBy(r => r.ProductCode),
                "product_desc" => rows.OrderByDescending(r => r.ProductCode),
                _              => rows.OrderByDescending(r => r.MovementDate).ThenByDescending(r => r.CreatedAt),
            };

            var list = sorted.ToList();
            var ps = pageSize ?? 20;
            var pageIdx = pageNumber ?? 1;
            var pageItems = list.Skip((pageIdx - 1) * ps).Take(ps).ToList();
            var paged = new PaginatedList<StockMovementRowVm>(pageItems, list.Count, pageIdx, ps);
            return View(paged);
        }

        public async Task<IActionResult> Export(
            string format = "xlsx",
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? typeCode = null,
            string? productCode = null,
            string? docNo = null,
            string? locationCode = null,
            string? lotNo = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var rows = (await BuildQueryAsync(plantId, dateFrom, dateTo, typeCode, productCode, docNo, locationCode, lotNo))
                .OrderByDescending(r => r.MovementDate).ThenByDescending(r => r.CreatedAt).ToList();

            var headers = PzText.Get("Inv_ExportHeaders_Movement").Split('|');
            var body = rows.Select(r => new object?[]
            {
                r.MovementDate.ToString("yyyy-MM-dd"),
                $"{r.TypeCode} {r.TypeName}",
                r.Direction >= 0 ? "+" : "-",
                r.DocNo, r.DocLineNo,
                r.ProductCode, r.ProductName,
                $"{r.WhCode}/{r.LocationCode}", r.LotNo,
                r.Qty, r.UomCode,
                r.StockQty, r.StockUomCode,
                r.UnitCost, r.TotalCost,
                r.Remark,
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = body, Title = "StockMovement" };
            return format?.ToLowerInvariant() switch
            {
                "csv"  => File(_exportService.ToCsv(req),  "text/csv",         "StockMovement.csv"),
                "json" => File(_exportService.ToJson(req), "application/json", "StockMovement.json"),
                _      => File(_exportService.ToExcel(req),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "StockMovement.xlsx"),
            };
        }

        public async Task<IActionResult> Print(
            DateOnly? dateFrom = null,
            DateOnly? dateTo = null,
            string? typeCode = null,
            string? productCode = null,
            string? docNo = null,
            string? locationCode = null,
            string? lotNo = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var rows = (await BuildQueryAsync(plantId, dateFrom, dateTo, typeCode, productCode, docNo, locationCode, lotNo))
                .OrderByDescending(r => r.MovementDate).ThenByDescending(r => r.CreatedAt).ToList();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.DateFrom     = dateFrom;
            ViewBag.DateTo       = dateTo;
            ViewBag.TypeCode     = typeCode;
            ViewBag.ProductCode  = productCode;
            ViewBag.DocNo        = docNo;
            ViewBag.LocationCode = locationCode;
            ViewBag.LotNo        = lotNo;

            var qr = Url.Action("Index", null,
                new { dateFrom, dateTo, typeCode, productCode, docNo, locationCode, lotNo, searched = true }, Request.Scheme)!;
            await PreparePrintViewBagAsync(qr);
            return View(rows);
        }

        private async Task<List<StockMovementRowVm>> BuildQueryAsync(
            string plantId,
            DateOnly? dateFrom, DateOnly? dateTo,
            string? typeCode, string? productCode, string? docNo,
            string? locationCode, string? lotNo)
        {
            var q = _context.StockMovements.AsNoTracking().AsQueryable();

            if (dateFrom.HasValue) q = q.Where(m => m.MovementDate >= dateFrom.Value);
            if (dateTo.HasValue)   q = q.Where(m => m.MovementDate <= dateTo.Value);
            if (!string.IsNullOrWhiteSpace(typeCode))     q = q.Where(m => m.TypeCode == typeCode);
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                var lower = productCode.ToLower();
                q = q.Where(m => m.ProductCode.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(docNo))
            {
                var lower = docNo.ToLower();
                q = q.Where(m => m.DocNo.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(locationCode))
            {
                var lower = locationCode.ToLower();
                q = q.Where(m => m.LocationCode.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(lotNo))
            {
                var lower = lotNo.ToLower();
                q = q.Where(m => m.LotNo.ToLower().Contains(lower));
            }

            var raw = await q.Select(m => new
            {
                m.MovementId, m.MovementDate, m.CreatedAt,
                m.TypeCode, m.TypeCategory, m.Direction,
                m.DocNo, m.DocLineNo, m.SourceDocNo,
                m.ProductCode, m.LocationCode, m.LotNo,
                m.UomCode, m.Qty, m.StockUomCode, m.StockQty,
                m.UnitCost, m.TotalCost, m.CreatedBy, m.Remark,
            }).ToListAsync();

            var typeMap = await _context.InventoryTransactionTypes.AsNoTracking()
                .Where(t => raw.Select(r => r.TypeCode).Contains(t.TypeCode))
                .ToDictionaryAsync(t => t.TypeCode, t => t.TypeName);

            var productMap = await _context.Products.AsNoTracking()
                .Where(p => raw.Select(r => r.ProductCode).Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName);

            var locMap = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => raw.Select(r => r.LocationCode).Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => l.WhCode);

            return raw.Select(r => new StockMovementRowVm
            {
                MovementId   = r.MovementId,
                MovementDate = r.MovementDate,
                CreatedAt    = r.CreatedAt,
                TypeCode     = r.TypeCode,
                TypeName     = typeMap.TryGetValue(r.TypeCode, out var tn) ? tn : "",
                TypeCategory = r.TypeCategory,
                Direction    = r.Direction,
                DocNo        = r.DocNo,
                DocLineNo    = r.DocLineNo,
                SourceDocNo  = r.SourceDocNo,
                ProductCode  = r.ProductCode,
                ProductName  = productMap.TryGetValue(r.ProductCode, out var pn) ? pn : "",
                WhCode       = locMap.TryGetValue(r.LocationCode, out var wh) ? wh : "",
                LocationCode = r.LocationCode,
                LotNo        = r.LotNo,
                Qty          = r.Qty,
                UomCode      = r.UomCode,
                StockQty     = r.StockQty,
                StockUomCode = r.StockUomCode,
                UnitCost     = r.UnitCost,
                TotalCost    = r.TotalCost,
                CreatedBy    = r.CreatedBy,
                Remark       = r.Remark,
            }).ToList();
        }
    }
}
