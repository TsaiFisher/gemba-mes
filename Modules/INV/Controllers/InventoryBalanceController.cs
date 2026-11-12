using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// 庫存餘額查詢（唯讀）。依廠別過濾；
    /// 顯示品號 × 倉庫 × 儲位 × 批號 的 QtyOnHand / UnitCost；本頁品號另附全廠預留／可用彙總（預留不分儲位）。
    /// </summary>
    [Authorize]
    public class InventoryBalanceController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IInventoryReservationService _reservations;

        private const string MenuKey = "InventoryBalance";

        public InventoryBalanceController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IExportService exportService,
            IUomService uomService,
            ICurrencyService currencyService,
            IInventoryReservationService reservations)
        {
            _reservations = reservations;
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _exportService = exportService;
            _uomService = uomService;
            _currencyService = currencyService;
        }

        public async Task<IActionResult> Index(
            string? productCode = null,
            string? whCode = null,
            string? locationCode = null,
            string? lotNo = null,
            bool includeZero = false,
            int? pageNumber = null,
            int? pageSize = null,
            string? sortOrder = null,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.ProductCode     = productCode;
            ViewBag.WhCode          = whCode;
            ViewBag.LocationCode    = locationCode;
            ViewBag.LotNo           = lotNo;
            ViewBag.IncludeZero     = includeZero;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.ProductSort     = sortOrder == "product_asc"  ? "product_desc" : "product_asc";
            ViewBag.LocationSort    = sortOrder == "location_asc" ? "location_desc" : "location_asc";
            ViewBag.QtySort         = sortOrder == "qty_asc"      ? "qty_desc" : "qty_asc";

            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking()
                .Where(w => w.IsActive)
                .OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName })
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

            if (!searched) return View((PaginatedList<InventoryBalanceRowVm>?)null);

            var plantId = _userContext.PlantId ?? "";
            var rows = await BuildQueryAsync(plantId, productCode, whCode, locationCode, lotNo, includeZero);

            IEnumerable<InventoryBalanceRowVm> sorted = sortOrder switch
            {
                "product_desc"  => rows.OrderByDescending(r => r.ProductCode),
                "location_asc"  => rows.OrderBy(r => r.WhCode).ThenBy(r => r.LocationCode),
                "location_desc" => rows.OrderByDescending(r => r.WhCode).ThenByDescending(r => r.LocationCode),
                "qty_asc"       => rows.OrderBy(r => r.QtyOnHand),
                "qty_desc"      => rows.OrderByDescending(r => r.QtyOnHand),
                _               => rows.OrderBy(r => r.ProductCode).ThenBy(r => r.LocationCode),
            };

            var list = sorted.ToList();
            var ps = pageSize ?? 20;
            var pageIdx = pageNumber ?? 1;
            var pageItems = list.Skip((pageIdx - 1) * ps).Take(ps).ToList();
            var paged = new PaginatedList<InventoryBalanceRowVm>(pageItems, list.Count, pageIdx, ps);

            // 本頁出現的品號：全廠預留與可用（預留沒有記儲位，不能拆到上面的逐儲位列）
            var pageProducts = pageItems.GroupBy(r => r.ProductCode).Select(g => g.First()).ToList();
            var avail = await _reservations.GetPlantAvailabilityAsync(plantId, pageProducts.Select(r => r.ProductCode).ToList());
            ViewBag.ProductSummary = pageProducts.Select(r => new InventoryBalanceProductSummaryVm
            {
                ProductCode    = r.ProductCode,
                ProductName    = r.ProductName,
                StockUom       = r.StockUom,
                PlantOnHand    = avail[r.ProductCode].OnHand,
                PlantReserved  = avail[r.ProductCode].Reserved,
                PlantAvailable = avail[r.ProductCode].Available,
            }).OrderBy(r => r.ProductCode).ToList();
            return View(paged);
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 6;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            // 單位成本通常比金額多 2~4 位（含小單價計算誤差），取本幣 dp + 4，最少 4 位
            return map.TryGetValue(baseCurrency, out var dp) ? Math.Max(dp + 4, 4) : 6;
        }

        public async Task<IActionResult> Export(
            string format = "xlsx",
            string? productCode = null,
            string? whCode = null,
            string? locationCode = null,
            string? lotNo = null,
            bool includeZero = false)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var rows = (await BuildQueryAsync(plantId, productCode, whCode, locationCode, lotNo, includeZero))
                .OrderBy(r => r.ProductCode).ThenBy(r => r.LocationCode).ToList();

            var headers = PzText.Get("Inv_ExportHeaders_Balance").Split('|');
            var body = rows.Select(r => new object?[]
            {
                r.ProductCode, r.ProductName, r.WhCode, r.LocationCode, r.LotNo,
                r.QtyOnHand, r.UnitCost,
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = body, Title = "InventoryBalance" };
            return format?.ToLowerInvariant() switch
            {
                "csv"  => File(_exportService.ToCsv(req),   "text/csv",         "InventoryBalance.csv"),
                "json" => File(_exportService.ToJson(req),  "application/json", "InventoryBalance.json"),
                _      => File(_exportService.ToExcel(req),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "InventoryBalance.xlsx"),
            };
        }

        public async Task<IActionResult> Print(
            string? productCode = null,
            string? whCode = null,
            string? locationCode = null,
            string? lotNo = null,
            bool includeZero = false)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId ?? "";
            var rows = (await BuildQueryAsync(plantId, productCode, whCode, locationCode, lotNo, includeZero))
                .OrderBy(r => r.ProductCode).ThenBy(r => r.LocationCode).ToList();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.ProductCode  = productCode;
            ViewBag.WhCode       = whCode;
            ViewBag.LocationCode = locationCode;
            ViewBag.LotNo        = lotNo;
            ViewBag.IncludeZero  = includeZero;

            var qr = Url.Action("Index", null,
                new { productCode, whCode, locationCode, lotNo, includeZero, searched = true }, Request.Scheme)!;
            await PreparePrintViewBagAsync(qr);
            return View(rows);
        }

        private async Task<List<InventoryBalanceRowVm>> BuildQueryAsync(
            string plantId, string? productCode, string? whCode, string? locationCode, string? lotNo, bool includeZero)
        {
            var q = _context.Inventories.AsNoTracking().AsQueryable();

            if (!includeZero)
                q = q.Where(i => i.QtyOnHand != 0);

            if (!string.IsNullOrWhiteSpace(productCode))
            {
                var lower = productCode.ToLower();
                q = q.Where(i => i.ProductCode.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(locationCode))
            {
                var lower = locationCode.ToLower();
                q = q.Where(i => i.LocationCode.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(lotNo))
            {
                var lower = lotNo.ToLower();
                q = q.Where(i => i.LotNo.ToLower().Contains(lower));
            }

            var rows = await q.Select(i => new
            {
                i.ProductCode, i.LocationCode, i.LotNo,
                i.QtyOnHand, i.UnitCost,
            }).ToListAsync();

            var productMap = await _context.Products.AsNoTracking()
                .Where(p => rows.Select(r => r.ProductCode).Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => new { p.ProductName, p.StockUom });

            var locMap = await _context.StorageLocations.ForCurrentPlant(_context).AsNoTracking()
                .Where(l => rows.Select(r => r.LocationCode).Contains(l.LocationCode))
                .ToDictionaryAsync(l => l.LocationCode, l => new { l.WhCode, l.LocationName });

            var result = rows.Select(r =>
            {
                productMap.TryGetValue(r.ProductCode, out var p);
                locMap.TryGetValue(r.LocationCode, out var l);
                return new InventoryBalanceRowVm
                {
                    ProductCode  = r.ProductCode,
                    ProductName  = p?.ProductName ?? "",
                    StockUom     = p?.StockUom ?? "",
                    WhCode       = l?.WhCode ?? "",
                    LocationCode = r.LocationCode,
                    LocationName = l?.LocationName ?? "",
                    LotNo        = r.LotNo,
                    QtyOnHand    = r.QtyOnHand,
                    UnitCost     = r.UnitCost,
                };
            }).ToList();

            if (!string.IsNullOrWhiteSpace(whCode))
                result = result.Where(r => r.WhCode == whCode).ToList();

            return result;
        }
    }
}
