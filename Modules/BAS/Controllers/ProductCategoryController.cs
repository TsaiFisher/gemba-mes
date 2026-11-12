using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class ProductCategoryController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ProductCategory";

        public ProductCategoryController(ApplicationDbContext context,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        private async Task PrepareFormDropdowns()
        {
            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName }).ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            ViewBag.LocationOptions = await _context.StorageLocations
                .AsNoTracking().Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();
        }

        // GET: /ProductCategory
        public async Task<IActionResult> Index(
            string? searchTerm,
            int? pageNumber, int? pageSize,
            string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm      = searchTerm;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.CodeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<ProductCategory>?)null);

            var query = _context.ProductCategories.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CategoryCode.ToLower().Contains(lower) ||
                    c.CategoryName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(c => c.CategoryCode),
                "name_asc"  => query.OrderBy(c => c.CategoryName),
                "name_desc" => query.OrderByDescending(c => c.CategoryName),
                _           => query.OrderBy(c => c.CategoryCode),
            };

            return View(await PaginatedList<ProductCategory>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        // GET: /ProductCategory/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            var model = new ProductCategory { IsActive = true };
            if (IsAjax) return PartialView("_ProductCategoryForm", model);
            return View("_ProductCategoryForm", model);
        }


        /// <summary>
        /// 預設倉庫／儲位／庫存單位必須存在、屬於本廠、且未停用。
        ///
        /// 這三欄在畫面上是下拉，選項已被過濾，所以正常操作走不到這裡；擋的是直接 POST。
        /// 值得擋是因為錯了不會報錯只會算錯：預設倉指到別廠的倉之後，
        /// 每一張用這個類別開的單都會把料收到本廠查不到的位置。
        /// </summary>
        private async Task ValidateReferencesAsync(ProductCategory model)
        {
            var plantId = _userContext.PlantId;

            var wh = await MasterReferenceGuard.CheckWarehouseAsync(_context, model.DefaultWarehouseCode, plantId);
            AddRefError(nameof(model.DefaultWarehouseCode), wh, model.DefaultWarehouseCode);

            var loc = await MasterReferenceGuard.CheckLocationAsync(
                _context, model.DefaultWarehouseCode, model.DefaultLocationCode, plantId);
            AddRefError(nameof(model.DefaultLocationCode), loc, model.DefaultLocationCode);

            var uom = await MasterReferenceGuard.CheckUomAsync(_context, model.DefaultStockUomCode);
            AddRefError(nameof(model.DefaultStockUomCode), uom, model.DefaultStockUomCode);
        }

        private void AddRefError(string field, MasterRefStatus status, string? code)
        {
            var key = MasterReferenceGuard.MessageKey(status);
            if (key == null) return;
            ModelState.AddModelError(field, string.Format(_localizer[key].Value, code));
        }

        // POST: /ProductCategory/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(ProductCategory.CategoryCode), nameof(ProductCategory.CategoryName),
            nameof(ProductCategory.EnableLotTracking), nameof(ProductCategory.DefaultWarehouseCode),
            nameof(ProductCategory.DefaultLocationCode), nameof(ProductCategory.DefaultStockUomCode),
            nameof(ProductCategory.CostMethod), nameof(ProductCategory.Remarks), nameof(ProductCategory.IsActive))] ProductCategory model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await ValidateReferencesAsync(model);
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ProductCategoryForm", model);
                return View("_ProductCategoryForm", model);
            }

            var exists = await _context.ProductCategories.AnyAsync(c => c.CategoryCode == model.CategoryCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.CategoryCode), _localizer["DuplicateCode"].Value);
                await PrepareFormDropdowns();
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ProductCategoryForm", model);
                return View("_ProductCategoryForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.ProductCategories.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /ProductCategory/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.ProductCategories.FindAsync(code);
            if (rec == null) return NotFound();

            await PrepareFormDropdowns();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ProductCategoryForm", rec);
            return View("_ProductCategoryForm", rec);
        }

        // POST: /ProductCategory/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, ProductCategory updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            await ValidateReferencesAsync(updated);
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ProductCategoryForm", updated);
                return View("_ProductCategoryForm", updated);
            }

            var rec = await _context.ProductCategories.FindAsync(code);
            if (rec == null) return NotFound();

            rec.CategoryName         = updated.CategoryName;
            rec.EnableLotTracking    = updated.EnableLotTracking;
            rec.DefaultWarehouseCode = updated.DefaultWarehouseCode;
            rec.DefaultLocationCode  = updated.DefaultLocationCode;
            rec.DefaultStockUomCode  = updated.DefaultStockUomCode;
            rec.CostMethod           = updated.CostMethod;
            rec.Remarks              = updated.Remarks;
            rec.IsActive             = updated.IsActive;
            rec.UpdatedAt            = DateTime.UtcNow;

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /ProductCategory/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var inUse = await _context.Products.AnyAsync(p => p.CategoryCode == code);
            if (inUse)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(409);
            }

            var rec = await _context.ProductCategories.FindAsync(code);
            if (rec != null)
            {
                _context.ProductCategories.Remove(rec);
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

        // GET: /ProductCategory/GetLocationsByWarehouse?whCode=xxx
        [HttpGet]
        public async Task<IActionResult> GetLocationsByWarehouse(string? whCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var query = _context.StorageLocations.AsNoTracking().Where(l => l.IsActive);
            if (!string.IsNullOrEmpty(whCode))
                query = query.Where(l => l.WhCode == whCode);

            var list = await query.OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();
            return Json(list);
        }

        // GET: /ProductCategory/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.ProductCategories.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CategoryCode.ToLower().Contains(lower) ||
                    c.CategoryName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(c => c.CategoryCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.ProductCategory.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["CategoryCode"].Value, L["CategoryName"].Value, L["EnableLotTracking"].Value,
                L["DefaultWarehouseCode"].Value, L["DefaultLocationCode"].Value,
                L["DefaultStockUomCode"].Value, L["CostMethod"].Value, L["IsActive"].Value
            };
            var rows = data.Select(c => new object?[]
            {
                c.CategoryCode, c.CategoryName, c.EnableLotTracking, c.DefaultWarehouseCode,
                c.DefaultLocationCode, c.DefaultStockUomCode, c.CostMethod.ToString(), c.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "ProductCategory" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "ProductCategory.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "ProductCategory.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "ProductCategory.json"),
            };
        }
    }
}
