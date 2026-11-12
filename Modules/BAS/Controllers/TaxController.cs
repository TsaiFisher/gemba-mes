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
    public class TaxController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "TaxMaster";

        public TaxController(ApplicationDbContext context, IApprovalService approvalService,
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


        // GET: /Tax
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
            ViewBag.RateSort = sortOrder == "rate_asc" ? "rate_desc" : "rate_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<Tax>?)null);

            var query = _context.Taxes.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.TaxCode.ToLower().Contains(lower) ||
                    t.TaxName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.TaxCode),
                "name_asc"  => query.OrderBy(t => t.TaxName),
                "name_desc" => query.OrderByDescending(t => t.TaxName),
                "rate_asc"  => query.OrderBy(t => t.TaxRate),
                "rate_desc" => query.OrderByDescending(t => t.TaxRate),
                _           => query.OrderBy(t => t.TaxCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Tax>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Tax/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Tax { IsActive = true };
            if (IsAjax) return PartialView("_TaxForm", model);
            return View("_TaxForm", model);
        }

        // POST: /Tax/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Tax.TaxCode), nameof(Tax.TaxName), nameof(Tax.TaxRate),
            nameof(Tax.Remarks), nameof(Tax.IsActive))] Tax model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_TaxForm", model);
                return View("_TaxForm", model);
            }

            var exists = await _context.Taxes.AnyAsync(t => t.TaxCode == model.TaxCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.TaxCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_TaxForm", model);
                return View("_TaxForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            var config = await _approvalService.GetApprovalConfig("Tax", _userContext.PlantId);
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                model.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                model.IsActive = false;
                _context.Taxes.Add(model);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                    return StatusCode(500);
                }
                var request = await _approvalService.SubmitForApproval(
                    model.TaxCode, "Tax", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                model.ApprovalRequestId = request.Id;
                await _context.SaveChangesAsync();
                if (IsAjax) return Json(new { success = true, message = _localizer["SubmittedForApprovalCreate"].Value });
            }
            else
            {
                model.ApprovalStatus = DocumentApprovalStatus.Approved;
                _context.Taxes.Add(model);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            }
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Tax/Details/{code}  — 唯讀檢視（供簽核頁面 Side Panel 使用）
        public async Task<IActionResult> Details(string code)
        {
            if (!await CanReadDocumentAsync(MenuKey, code)) return Forbid();
            var rec = await _context.Taxes.FindAsync(code);
            if (rec == null) return NotFound();
            ViewBag.IsEdit = true;
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_TaxForm", rec);
            return View("_TaxForm", rec);
        }

        // GET: /Tax/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Taxes.FindAsync(code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                TempData["ErrorMessage"] = _localizer["PendingApprovalCannotEdit"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_TaxForm", rec);
            return View("_TaxForm", rec);
        }

        // POST: /Tax/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Tax updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_TaxForm", updated);
                return View("_TaxForm", updated);
            }

            var rec = await _context.Taxes.FindAsync(code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            rec.TaxName   = updated.TaxName;
            rec.TaxRate   = updated.TaxRate;
            rec.Remarks   = updated.Remarks;
            rec.IsActive  = updated.IsActive;
            rec.UpdatedAt = DateTime.UtcNow;

            var config = await _approvalService.GetApprovalConfig("Tax", _userContext.PlantId);
            string msg;
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                rec.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                rec.IsActive = false;
                await _context.SaveChangesAsync();
                var request = await _approvalService.SubmitForApproval(
                    code, "Tax", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                rec.ApprovalRequestId = request.Id;
                msg = _localizer["SubmittedForApprovalUpdate"].Value;
            }
            else
            {
                msg = _localizer["UpdateSuccess"].Value;
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            if (IsAjax) return Json(new { success = true, message = msg });
            TempData["SuccessMessage"] = msg;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Tax/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Taxes.FindAsync(code);
            if (rec == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
                TempData["ErrorMessage"] = _localizer["PendingApprovalCannotEdit"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            // 往來廠商預設稅別、銷售訂單預設稅別、各模組單據明細皆以 TaxCode 鬆散字串參照（無 DB 層 FK 可攔截），
            // 主動查核，避免稅別刪除後留下孤兒資料。
            if (await IsTaxInUseAsync(code))
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["TaxInUseCannotDelete"].Value });
                TempData["ErrorMessage"] = _localizer["TaxInUseCannotDelete"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            _context.Taxes.Remove(rec);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>稅別是否仍被往來廠商預設稅別、銷售訂單或採購/銷售/委外/應收應付單據明細引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsTaxInUseAsync(string code)
        {
            if (await _context.Companies.AnyAsync(c => c.TaxCode == code))
                return true;
            if (await _context.SalesOrders.IgnoreQueryFilters().AnyAsync(o => o.TaxCode == code))
                return true;

            if (await _context.PurchaseOrderLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.GoodsReceiptLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.GoodsReturnLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.OutsourceReceiptLines.AnyAsync(l => l.TaxCode == code))
                return true;

            if (await _context.SalesOrderLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.GoodsShipmentLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.SalesReturnLines.AnyAsync(l => l.TaxCode == code))
                return true;

            if (await _context.APInvoiceLines.AnyAsync(l => l.TaxCode == code))
                return true;
            if (await _context.ARInvoiceLines.AnyAsync(l => l.TaxCode == code))
                return true;

            return false;
        }

        // GET: /Tax/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Taxes.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.TaxCode.ToLower().Contains(lower) ||
                    t.TaxName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.TaxCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Tax.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["TaxCode"].Value, L["TaxName"].Value, L["TaxRate"].Value,
                L["Remarks"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.TaxCode, t.TaxName, t.TaxRate, t.Remarks, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Tax" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Tax.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Tax.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Tax.json"),
            };
        }
    }
}
