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
    public class InventoryTransactionTypeController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "InventoryTransactionType";

        public InventoryTransactionTypeController(ApplicationDbContext context, IApprovalService approvalService,
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


        // GET: /InventoryTransactionType
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

            if (!searched) return View((PaginatedList<InventoryTransactionType>?)null);

            var query = _context.InventoryTransactionTypes.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.TypeCode.ToLower().Contains(lower) ||
                    t.TypeName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.TypeCode),
                "name_asc"  => query.OrderBy(t => t.TypeName),
                "name_desc" => query.OrderByDescending(t => t.TypeName),
                _           => query.OrderBy(t => t.TypeCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<InventoryTransactionType>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /InventoryTransactionType/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new InventoryTransactionType { IsActive = true };
            if (IsAjax) return PartialView("_InventoryTransactionTypeForm", model);
            return View("_InventoryTransactionTypeForm", model);
        }

        // POST: /InventoryTransactionType/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(InventoryTransactionType.TypeCode), nameof(InventoryTransactionType.TypeName),
            nameof(InventoryTransactionType.TypeCategory), nameof(InventoryTransactionType.Direction),
            nameof(InventoryTransactionType.RequireLot), nameof(InventoryTransactionType.RequireDoc),
            nameof(InventoryTransactionType.SourceTypeCode), nameof(InventoryTransactionType.IsActive))] InventoryTransactionType model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_InventoryTransactionTypeForm", model);
                return View("_InventoryTransactionTypeForm", model);
            }

            var exists = await _context.InventoryTransactionTypes.AnyAsync(t => t.TypeCode == model.TypeCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.TypeCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_InventoryTransactionTypeForm", model);
                return View("_InventoryTransactionTypeForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.InventoryTransactionTypes.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /InventoryTransactionType/Edit/{id}
        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.InventoryTransactionTypes.FindAsync(id);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_InventoryTransactionTypeForm", rec);
            return View("_InventoryTransactionTypeForm", rec);
        }

        // POST: /InventoryTransactionType/Edit/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, InventoryTransactionType updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_InventoryTransactionTypeForm", updated);
                return View("_InventoryTransactionTypeForm", updated);
            }

            var rec = await _context.InventoryTransactionTypes.FindAsync(id);
            if (rec == null) return NotFound();

            // 系統內建類別的**過帳語意**不可改：Direction 決定每一筆庫存異動的正負號、
            // TypeCategory 決定要不要產生分錄（Production/Outsource 回空、Purchase 掛 GR/IR）。
            // 改掉其中一個，發料會變成入庫、或整批分錄悄悄消失，而且已過帳的舊單不會回頭修正。
            // 刪除早就擋了（見下方 DeleteConfirmed），改語意其實比刪掉更危險——刪不掉的東西
            // 被改成另一個意思，看起來一切正常。名稱與啟用狀態不影響過帳，仍可修改。
            if (rec.IsSystemType
                && (rec.Direction != updated.Direction
                 || rec.TypeCategory != updated.TypeCategory
                 || rec.SourceTypeCode != updated.SourceTypeCode))
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SystemTypeSemanticsLocked"].Value });
                return StatusCode(409);
            }

            rec.TypeName       = updated.TypeName;
            rec.RequireLot     = updated.RequireLot;
            rec.RequireDoc     = updated.RequireDoc;
            rec.IsActive       = updated.IsActive;
            if (!rec.IsSystemType)
            {
                rec.TypeCategory   = updated.TypeCategory;
                rec.Direction      = updated.Direction;
                rec.SourceTypeCode = updated.SourceTypeCode;
            }
            rec.UpdatedAt      = DateTime.UtcNow;

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

        // POST: /InventoryTransactionType/Delete/{id}
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.InventoryTransactionTypes.FindAsync(id);
            if (rec != null)
            {
                if (rec.IsSystemType)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["SystemRecordCannotDelete"].Value });
                    return StatusCode(409);
                }
                _context.InventoryTransactionTypes.Remove(rec);
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

        // GET: /InventoryTransactionType/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.InventoryTransactionTypes.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.TypeCode.ToLower().Contains(lower) ||
                    t.TypeName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.TypeCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.InventoryTransactionType.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["TypeCode"].Value, L["TypeName"].Value, L["TypeCategory"].Value,
                L["SourceTypeCode"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.TypeCode, t.TypeName, t.TypeCategory, t.SourceTypeCode, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "InventoryTransactionType" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "InventoryTransactionType.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "InventoryTransactionType.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "InventoryTransactionType.json"),
            };
        }
    }
}
