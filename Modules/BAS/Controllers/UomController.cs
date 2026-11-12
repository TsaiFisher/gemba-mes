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
    public class UomController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IUomService _uomService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private const string MenuKey = "UomMaster";

        // GetConversionRate 的合法呼叫端（R25）：單位維護畫面 ＋ 會做單位換算的 6 張單據 _Form。
        // 只鎖 UomMaster 會讓「有單據但沒有 BAS」的角色在開單時換算失敗，
        // 且前端只是數量帶不出來、不報錯（靜默壞掉）。
        private static readonly string[] ConversionReaderKeys =
        {
            MenuKey,
            "SalesOrder", "GoodsShipment", "SalesReturn", "InternalOrder",
            "GoodsReceipt", "GoodsReturn",
        };

        public UomController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IUomService uomService, IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _uomService = uomService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        // GET: /Uom
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

            ViewBag.UomCodeList = await _context.Uoms
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            // 品號特定換算用的選單。停用品號一併排除（與上面單位清單同樣的理由：
            // 這是「還能不能挑選」那一層，不是「已存在的資料怎麼解讀」那一層）。
            ViewBag.ProductCodeList = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            if (!searched) return View((PaginatedList<Uom>?)null);

            var query = _context.Uoms.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(u =>
                    u.UomCode.ToLower().Contains(lower) ||
                    u.UomName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(u => u.UomCode),
                "name_asc"  => query.OrderBy(u => u.UomName),
                "name_desc" => query.OrderByDescending(u => u.UomName),
                _           => query.OrderBy(u => u.UomCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Uom>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Uom/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Uom { IsActive = true };
            if (IsAjax) return PartialView("_UomForm", model);
            return View("_UomForm", model);
        }

        // POST: /Uom/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Uom.UomCode), nameof(Uom.UomName), nameof(Uom.Remarks),
            nameof(Uom.DecimalPlaces), nameof(Uom.IsActive))] Uom model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_UomForm", model);
                return View("_UomForm", model);
            }

            var exists = await _context.Uoms.AnyAsync(u => u.UomCode == model.UomCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.UomCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_UomForm", model);
                return View("_UomForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            _context.Uoms.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            _uomService.InvalidateDecimalPlacesCache();
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Uom/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Uoms.FindAsync(code);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_UomForm", rec);
            return View("_UomForm", rec);
        }

        // POST: /Uom/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Uom updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_UomForm", updated);
                return View("_UomForm", updated);
            }

            var rec = await _context.Uoms.FindAsync(code);
            if (rec == null) return NotFound();

            rec.UomName      = updated.UomName;
            rec.Remarks      = updated.Remarks;
            rec.DecimalPlaces = updated.DecimalPlaces;
            rec.IsActive     = updated.IsActive;
            rec.UpdatedAt    = DateTime.UtcNow;

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            _uomService.InvalidateDecimalPlacesCache();
            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Uom/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Uoms.FindAsync(code);
            if (rec != null)
            {
                // 品號庫存單位、單位換算、途程用料皆以 UomCode 鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免計量單位刪除後留下孤兒資料或數量欄位精度失準。
                if (await IsUomInUseAsync(code))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["UomInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["UomInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.Uoms.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                    return StatusCode(500);
                }
                _uomService.InvalidateDecimalPlacesCache();
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>計量單位是否仍被品號庫存單位、單位換算或途程用料引用中。</summary>
        private async Task<bool> IsUomInUseAsync(string code)
        {
            if (await _context.Products.AnyAsync(p => p.StockUom == code))
                return true;
            if (await _context.UomConversions.AnyAsync(c => c.FromUomCode == code || c.ToUomCode == code))
                return true;
            // 品號特定換算刻意沒建 FK cascade（見 ApplicationDbContextExtensions），
            // 所以引用檢查必須在這裡顯式涵蓋它，否則刪掉單位會留下永遠查不到的孤兒列。
            if (await _context.ProductUomConversions.AnyAsync(c => c.FromUomCode == code || c.ToUomCode == code))
                return true;
            if (await _context.RoutingBoms.AnyAsync(b => b.UomCode == code))
                return true;
            return false;
        }

        // GET: /Uom/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Uoms.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(u =>
                    u.UomCode.ToLower().Contains(lower) ||
                    u.UomName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(u => u.UomCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Uom.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["UomCode"].Value, L["UomName"].Value, L["DecimalPlaces"].Value,
                L["Remarks"].Value, L["IsActive"].Value
            };
            var rows = data.Select(u => new object?[]
            {
                u.UomCode, u.UomName, u.DecimalPlaces, u.Remarks, u.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Uom" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Uom.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Uom.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Uom.json"),
            };
        }

        // AJAX: GET /Uom/GetConversions?uomCode=xxx
        //
        // 回傳全域換算與品號特定換算兩種列，以 productCode 是否為空區分：
        // 空字串＝全域（UomConversions），有值＝該品號專屬（ProductUomConversions）。
        // 合在同一張表顯示是刻意的——維護人員關心的是「BOX 換到 PC 現在是幾比幾」，
        // 分成兩個分頁會讓「全域說 10、A 品號說 12」這種覆寫關係看不出來。
        [HttpGet]
        public async Task<IActionResult> GetConversions(string uomCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var global = await _context.UomConversions
                .AsNoTracking()
                .Where(c => c.FromUomCode == uomCode)
                .Select(c => new {
                    ProductCode = "",
                    c.FromUomCode,
                    c.ToUomCode,
                    c.Numerator,
                    c.Denominator
                })
                .ToListAsync();

            var perProduct = await _context.ProductUomConversions
                .AsNoTracking()
                .Where(c => c.FromUomCode == uomCode)
                .Select(c => new {
                    c.ProductCode,
                    c.FromUomCode,
                    c.ToUomCode,
                    c.Numerator,
                    c.Denominator
                })
                .ToListAsync();

            // 全域列在前，品號列依品號排序跟在後面。
            var list = global
                .OrderBy(c => c.ToUomCode)
                .Concat(perProduct.OrderBy(c => c.ProductCode).ThenBy(c => c.ToUomCode))
                .ToList();

            return Json(list);
        }

        // AJAX: POST /Uom/SaveConversion
        // productCode 留空 → 全域換算；有值 → 該品號的特定換算。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveConversion(string fromUomCode, string toUomCode, decimal numerator, decimal denominator, string? productCode = null)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(fromUomCode) || string.IsNullOrWhiteSpace(toUomCode))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            if (denominator == 0)
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            if (string.IsNullOrWhiteSpace(productCode))
            {
                var existing = await _context.UomConversions.FindAsync(fromUomCode, toUomCode);
                if (existing != null)
                {
                    existing.Numerator   = numerator;
                    existing.Denominator = denominator;
                }
                else
                {
                    _context.UomConversions.Add(new UomConversion
                    {
                        FromUomCode  = fromUomCode,
                        ToUomCode    = toUomCode,
                        Numerator    = numerator,
                        Denominator  = denominator
                    });
                }
            }
            else
            {
                // 品號不存在就不讓存：換算查找是以品號為鍵，打錯字的話這一列永遠不會被用到，
                // 而且不會有任何錯誤——正是本次要消滅的那種靜默失效。
                if (!await _context.Products.AnyAsync(p => p.ProductCode == productCode))
                    return Json(new { success = false, message = _localizer["OperationFailed"].Value });

                var existing = await _context.ProductUomConversions.FindAsync(productCode, fromUomCode, toUomCode);
                if (existing != null)
                {
                    existing.Numerator   = numerator;
                    existing.Denominator = denominator;
                }
                else
                {
                    _context.ProductUomConversions.Add(new ProductUomConversion
                    {
                        ProductCode  = productCode,
                        FromUomCode  = fromUomCode,
                        ToUomCode    = toUomCode,
                        Numerator    = numerator,
                        Denominator  = denominator
                    });
                }
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["SaveFailed"].Value });
            }

            return Json(new { success = true, message = _localizer["SaveSuccess"].Value });
        }

        // AJAX: POST /Uom/DeleteConversion
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConversion(string fromCode, string toCode, string? productCode = null)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            if (string.IsNullOrWhiteSpace(productCode))
            {
                var rec = await _context.UomConversions.FindAsync(fromCode, toCode);
                if (rec != null) _context.UomConversions.Remove(rec);
            }
            else
            {
                var rec = await _context.ProductUomConversions.FindAsync(productCode, fromCode, toCode);
                if (rec != null) _context.ProductUomConversions.Remove(rec);
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["DeleteFailed"].Value });
            }

            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // AJAX: GET /Uom/GetConversionRate?productCode=xxx&fromUom=xxx&toUom=xxx
        [HttpGet]
        public async Task<IActionResult> GetConversionRate(string productCode, string fromUom, string toUom)
        {
            if (!await _permissionService.CanReadAny(ConversionReaderKeys)) return Forbid();
            var rate = await _uomService.GetConversionRateAsync(productCode, fromUom, toUom);
            return Json(new { success = true, rate });
        }
    }
}
