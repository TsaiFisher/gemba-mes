using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class WarehouseController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private const string MenuKey = "WarehouseMaster";

        public WarehouseController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        // GET: /Warehouse
        public async Task<IActionResult> Index(
            string? searchTerm,
            int? pageNumber, int? pageSize,
            string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CodeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<Warehouse>?)null);

            var query = _context.Warehouses.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(w =>
                    w.WhCode.ToLower().Contains(lower) ||
                    w.WhName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(w => w.WhCode),
                "name_asc"  => query.OrderBy(w => w.WhName),
                "name_desc" => query.OrderByDescending(w => w.WhName),
                _           => query.OrderBy(w => w.WhCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Warehouse>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Warehouse/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Warehouse { IsActive = true };
            if (IsAjax) return PartialView("_WarehouseForm", model);
            return View("_WarehouseForm", model);
        }

        // POST: /Warehouse/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Warehouse model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_WarehouseForm", model);
                return View("_WarehouseForm", model);
            }

            var exists = await _context.Warehouses.AnyAsync(w => w.WhCode == model.WhCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.WhCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_WarehouseForm", model);
                return View("_WarehouseForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            _context.Warehouses.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Warehouse/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Warehouses.FindAsync(code);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_WarehouseForm", rec);
            return View("_WarehouseForm", rec);
        }

        // POST: /Warehouse/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Warehouse updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_WarehouseForm", updated);
                return View("_WarehouseForm", updated);
            }

            var rec = await _context.Warehouses.FindAsync(code);
            if (rec == null) return NotFound();

            rec.WhName          = updated.WhName;
            rec.WarehouseType   = updated.WarehouseType;
            rec.IsMrpWarehouse  = updated.IsMrpWarehouse;
            rec.Remarks         = updated.Remarks;
            rec.IsActive        = updated.IsActive;
            rec.UpdatedAt       = DateTime.UtcNow;

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Warehouse/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Warehouses.FindAsync(code);
            if (rec != null)
            {
                // 儲位主檔、庫存移轉（廠內/跨廠）、進貨/出貨明細等皆以 WhCode 鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免倉庫刪除後留下孤兒資料。儲位主檔為最權威的下層依賴（各單據多透過儲位存放倉庫代碼），
                // 另補查以倉庫代碼為主鍵欄位的移轉單頭，其餘大量單據明細之 WhCode 重複欄位不逐一列舉。
                if (await IsWarehouseInUseAsync(code))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["WarehouseInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["WarehouseInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.Warehouses.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                    return StatusCode(500);
                }
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>倉庫是否仍被儲位主檔、庫存移轉單（廠內/跨廠）或進貨/出貨明細引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsWarehouseInUseAsync(string code)
        {
            if (await _context.StorageLocations.AnyAsync(l => l.WhCode == code))
                return true;
            if (await _context.StockTransfers.IgnoreQueryFilters()
                    .AnyAsync(t => t.FromWhCode == code || t.ToWhCode == code))
                return true;
            if (await _context.CrossPlantTransfers.IgnoreQueryFilters()
                    .AnyAsync(t => t.FromWhCode == code || t.ToWhCode == code))
                return true;
            if (await _context.GoodsReceiptLines.AnyAsync(l => l.WhCode == code))
                return true;
            if (await _context.GoodsShipmentLines.AnyAsync(l => l.WhCode == code))
                return true;
            return false;
        }

        // GET: /Warehouse/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Warehouses.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(w =>
                    w.WhCode.ToLower().Contains(lower) ||
                    w.WhName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(w => w.WhCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Warehouse.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["WhCode"].Value, L["WhName"].Value, L["IsMrpWarehouse"].Value,
                L["Remarks"].Value, L["IsActive"].Value
            };
            var rows = data.Select(w => new object?[]
            {
                w.WhCode, w.WhName, w.IsMrpWarehouse, w.Remarks, w.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Warehouse" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Warehouse.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Warehouse.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Warehouse.json"),
            };
        }

        // AJAX: GET /Warehouse/GetLocations?whCode=xxx
        [HttpGet]
        public async Task<IActionResult> GetLocations(string whCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var list = await _context.StorageLocations
                .AsNoTracking()
                .Where(l => l.WhCode == whCode)
                .OrderBy(l => l.LocationCode)
                .Select(l => new {
                    l.WhCode,
                    l.LocationCode,
                    l.LocationName,
                    l.LocationType,
                    l.Remarks,
                    l.IsActive
                })
                .ToListAsync();
            return Json(list);
        }

        // AJAX: POST /Warehouse/SaveLocation
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLocation(string whCode, string locationCode, string locationName,
            string? locationType, string? remarks, bool isActive)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(whCode) || string.IsNullOrWhiteSpace(locationCode) || string.IsNullOrWhiteSpace(locationName))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            var existing = await _context.StorageLocations.FindAsync(whCode, locationCode);
            if (existing != null)
            {
                existing.LocationName = locationName;
                existing.LocationType = locationType;
                existing.Remarks      = remarks;
                existing.IsActive     = isActive;
            }
            else
            {
                _context.StorageLocations.Add(new StorageLocation
                {
                    WhCode       = whCode,
                    LocationCode = locationCode,
                    LocationName = locationName,
                    LocationType = locationType,
                    Remarks      = remarks,
                    IsActive     = isActive
                });
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["SaveFailed"].Value });
            }

            return Json(new { success = true, message = _localizer["SaveSuccess"].Value });
        }

        // AJAX: POST /Warehouse/DeleteLocation
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteLocation(string whCode, string locationCode)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.StorageLocations.FindAsync(whCode, locationCode);
            if (rec != null)
            {
                _context.StorageLocations.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    return Json(new { success = false, message = _localizer["DeleteFailed"].Value });
                }
            }
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }
    }
}
