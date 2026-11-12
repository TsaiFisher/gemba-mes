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
    public class CompanyController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "CompanyMaster";

        public CompanyController(ApplicationDbContext context, IApprovalService approvalService,
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


        private async Task PrepareFormDropdowns()
        {
            ViewBag.CurrencyOptions = await _context.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName })
                .ToListAsync();

            ViewBag.TaxOptions = await _context.Taxes
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.TaxCode)
                .Select(t => new { t.TaxCode, t.TaxName, t.TaxRate })
                .ToListAsync();

            ViewBag.PaymentTypeOptions = await _context.CodeEntries
                .AsNoTracking()
                .Where(e => e.Category == "payment_type" && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .ToDisplayOptionsAsync();
        }

        // GET: /Company
        public async Task<IActionResult> Index(
            string? searchTerm, string? companyType,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm   = searchTerm;
            ViewBag.CompanyType  = companyType;
            ViewBag.Searched     = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort  = sortOrder;
            ViewBag.CodeSort  = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort  = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            ViewBag.CurrencyNames = await _context.Currencies.AsNoTracking()
                .ToDictionaryAsync(c => c.CurrencyCode, c => c.CurrencyName);

            if (!searched) return View((PaginatedList<Company>?)null);

            var query = _context.Companies.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CompanyCode.ToLower().Contains(lower) ||
                    c.CompanyName.ToLower().Contains(lower) ||
                    (c.ShortName != null && c.ShortName.ToLower().Contains(lower)));
            }

            if (companyType == "customer")  query = query.Where(c => c.IsCustomer);
            if (companyType == "supplier")  query = query.Where(c => c.IsSupplier);

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(c => c.CompanyCode),
                "name_asc"  => query.OrderBy(c => c.CompanyName),
                "name_desc" => query.OrderByDescending(c => c.CompanyName),
                _           => query.OrderBy(c => c.CompanyCode),
            };

            return View(await PaginatedList<Company>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        // GET: /Company/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            var model = new Company { IsActive = true };
            if (IsAjax) return PartialView("_CompanyForm", model);
            return View("_CompanyForm", model);
        }

        // POST: /Company/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Company model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_CompanyForm", model);
                return View("_CompanyForm", model);
            }

            var exists = await _context.Companies.AnyAsync(c => c.CompanyCode == model.CompanyCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.CompanyCode), _localizer["DuplicateCode"].Value);
                await PrepareFormDropdowns();
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_CompanyForm", model);
                return View("_CompanyForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            var config = await _approvalService.GetApprovalConfig("Company", _userContext.PlantId);
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                model.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                model.IsActive = false;
                _context.Companies.Add(model);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                    return StatusCode(500);
                }
                var request = await _approvalService.SubmitForApproval(
                    model.CompanyCode, "Company", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                model.ApprovalRequestId = request.Id;
                await _context.SaveChangesAsync();
                if (IsAjax) return Json(new { success = true, message = _localizer["SubmittedForApprovalCreate"].Value });
            }
            else
            {
                model.ApprovalStatus = DocumentApprovalStatus.Approved;
                _context.Companies.Add(model);
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

        // GET: /Company/Details/{code}  — 唯讀檢視（供簽核頁面 Side Panel 使用）
        public async Task<IActionResult> Details(string code)
        {
            if (!await CanReadDocumentAsync(MenuKey, code)) return Forbid();
            var rec = await _context.Companies.FindAsync(code);
            if (rec == null) return NotFound();
            await PrepareFormDropdowns();
            ViewBag.IsEdit = true;
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_CompanyForm", rec);
            return View("_CompanyForm", rec);
        }

        // GET: /Company/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Companies.FindAsync(code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            await PrepareFormDropdowns();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_CompanyForm", rec);
            return View("_CompanyForm", rec);
        }

        // POST: /Company/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Company updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_CompanyForm", updated);
                return View("_CompanyForm", updated);
            }

            var rec = await _context.Companies.FindAsync(code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            rec.CompanyName    = updated.CompanyName;
            rec.ShortName      = updated.ShortName;
            rec.IsCustomer     = updated.IsCustomer;
            rec.IsSupplier     = updated.IsSupplier;
            rec.PaymentType    = updated.PaymentType;
            rec.CurrencyCode   = updated.CurrencyCode;
            rec.TaxCode        = updated.TaxCode;
            rec.TaxRate        = updated.TaxRate;
            rec.PaymentTermDays = updated.PaymentTermDays;
            rec.PaymentCreditDays = updated.PaymentCreditDays;
            rec.Address        = updated.Address;
            rec.InvoiceNo      = updated.InvoiceNo;
            rec.SalesPersonId  = updated.SalesPersonId;
            rec.AbcGrade       = updated.AbcGrade;
            rec.Remarks        = updated.Remarks;
            rec.IsActive       = updated.IsActive;
            rec.UpdatedAt      = DateTime.UtcNow;

            var config = await _approvalService.GetApprovalConfig("Company", _userContext.PlantId);
            string msg;
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                rec.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                rec.IsActive = false;
                await _context.SaveChangesAsync();
                var request = await _approvalService.SubmitForApproval(
                    code, "Company", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                rec.ApprovalRequestId = request.Id;
                msg = PzText.Get("Bas_CompanyResubmitted");
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
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Company/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Companies
                .Include(c => c.Contacts)
                .FirstOrDefaultAsync(c => c.CompanyCode == code);
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

            // 客戶/供應商代碼於採購、銷售、委外、應收應付等單據皆以 CompanyCode/SupplierCode/CustomerCode
            // 鬆散字串參照（無 DB 層 FK 可攔截），主動查核，避免往來廠商刪除後留下孤兒單據。
            if (await IsCompanyInUseAsync(code))
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CompanyInUseCannotDelete"].Value });
                TempData["ErrorMessage"] = _localizer["CompanyInUseCannotDelete"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            _context.Companies.Remove(rec);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>往來廠商是否仍被採購/銷售/委外/應收應付等單據引用中（供應商或客戶角色）；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsCompanyInUseAsync(string code)
        {
            // 採購側（供應商角色）
            if (await _context.PurchaseOrders.IgnoreQueryFilters().AnyAsync(o => o.SupplierCode == code))
                return true;
            if (await _context.GoodsReceipts.IgnoreQueryFilters().AnyAsync(r => r.SupplierCode == code))
                return true;
            if (await _context.GoodsReturns.IgnoreQueryFilters().AnyAsync(r => r.SupplierCode == code))
                return true;
            if (await _context.VendorPriceLists.AnyAsync(v => v.SupplierCode == code))
                return true;
            if (await _context.VendorEvaluations.AnyAsync(v => v.SupplierCode == code))
                return true;

            // 委外側（供應商角色）
            if (await _context.OutsourcePurchaseOrders.IgnoreQueryFilters().AnyAsync(o => o.SupplierCode == code))
                return true;
            if (await _context.OutsourceReceipts.IgnoreQueryFilters().AnyAsync(o => o.SupplierCode == code))
                return true;
            if (await _context.OutsourceQuotations.AnyAsync(o => o.SupplierCode == code))
                return true;
            if (await _context.OutsourceMaterialDocs.IgnoreQueryFilters().AnyAsync(o => o.SupplierCode == code))
                return true;

            // 銷售側（客戶角色）
            if (await _context.SalesOrders.IgnoreQueryFilters().AnyAsync(o => o.CompanyCode == code))
                return true;
            if (await _context.GoodsShipments.IgnoreQueryFilters().AnyAsync(s => s.CompanyCode == code))
                return true;
            if (await _context.SalesReturns.IgnoreQueryFilters().AnyAsync(r => r.CompanyCode == code))
                return true;

            // 財務側（應收應付）
            if (await _context.APInvoices.IgnoreQueryFilters().AnyAsync(i => i.SupplierCode == code))
                return true;
            if (await _context.APPayments.IgnoreQueryFilters().AnyAsync(p => p.SupplierCode == code))
                return true;
            if (await _context.ARInvoices.IgnoreQueryFilters().AnyAsync(i => i.CustomerCode == code))
                return true;
            if (await _context.ARReceipts.IgnoreQueryFilters().AnyAsync(r => r.CustomerCode == code))
                return true;

            return false;
        }

        // ── 聯絡人子表 ──────────────────────────────────────────────

        // GET: /Company/GetContacts?code=XXX
        [HttpGet]
        public async Task<IActionResult> GetContacts(string code)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var contacts = await _context.CompanyContacts.AsNoTracking()
                .Where(c => c.CompanyCode == code)
                .OrderBy(c => c.SeqNo)
                .Select(c => new {
                    c.Id, c.SeqNo, c.Role, c.ContactName,
                    c.Phone, c.Fax, c.Mobile, c.Address, c.Remarks
                })
                .ToListAsync();
            return Json(contacts);
        }

        // POST: /Company/SaveContact
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveContact(CompanyContact model)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (string.IsNullOrEmpty(model.CompanyCode))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            if (model.Id == 0)
            {
                // 新增：取最大 SeqNo + 1
                var maxSeq = await _context.CompanyContacts
                    .Where(c => c.CompanyCode == model.CompanyCode)
                    .Select(c => (int?)c.SeqNo)
                    .MaxAsync() ?? 0;
                model.SeqNo = maxSeq + 1;
                _context.CompanyContacts.Add(model);
            }
            else
            {
                var rec = await _context.CompanyContacts.FindAsync(model.Id);
                if (rec == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                rec.Role        = model.Role;
                rec.ContactName = model.ContactName;
                rec.Phone       = model.Phone;
                rec.Fax         = model.Fax;
                rec.Mobile      = model.Mobile;
                rec.Address     = model.Address;
                rec.Remarks     = model.Remarks;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SaveSuccess"].Value });
        }

        // POST: /Company/DeleteContact
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteContact(int id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.CompanyContacts.FindAsync(id);
            if (rec != null)
            {
                _context.CompanyContacts.Remove(rec);
                await _context.SaveChangesAsync();
            }
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // GET: /Company/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null, string? companyType = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Companies.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CompanyCode.ToLower().Contains(lower) ||
                    c.CompanyName.ToLower().Contains(lower));
            }
            if (companyType == "customer") query = query.Where(c => c.IsCustomer);
            if (companyType == "supplier") query = query.Where(c => c.IsSupplier);

            var data = await query.OrderBy(c => c.CompanyCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Company.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["CompanyCode"].Value, L["CompanyName"].Value, L["ShortName"].Value,
                L["IsCustomer"].Value, L["IsSupplier"].Value, L["CurrencyCode"].Value,
                L["TaxCode"].Value, L["PaymentTermDays"].Value, L["PaymentCreditDays"].Value, L["IsActive"].Value
            };
            var rows = data.Select(c => new object?[]
            {
                c.CompanyCode, c.CompanyName, c.ShortName, c.IsCustomer, c.IsSupplier,
                c.CurrencyCode, c.TaxCode, c.PaymentTermDays, c.PaymentCreditDays, c.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Company" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Company.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Company.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Company.json"),
            };
        }

        // GET: /Company/GetAddress?companyCode=XXX
        [HttpGet]
        public async Task<IActionResult> GetAddress(string companyCode)
        {
            if (!await _permissionService.CanReadAny(MenuKey, "PurchaseOrder")) return Forbid();
            var address = await _context.Companies
                .AsNoTracking()
                .Where(c => c.CompanyCode == companyCode)
                .Select(c => c.Address)
                .FirstOrDefaultAsync();
            return Json(new { address = address ?? "" });
        }
    }
}
