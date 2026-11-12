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
    public class CurrencyController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private const string MenuKey = "CurrencyMaster";

        public CurrencyController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            ICurrencyService currencyService, IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _currencyService = currencyService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        // GET: /Currency
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

            var baseCurrencyParam = await _context.SystemParams
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Key == ProjectZSystemParamKeys.BaseCurrency);
            ViewBag.BaseCurrency = baseCurrencyParam?.Value ?? "";

            if (!searched) return View((PaginatedList<Currency>?)null);

            var query = _context.Currencies.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CurrencyCode.ToLower().Contains(lower) ||
                    c.CurrencyName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(c => c.CurrencyCode),
                "name_asc"  => query.OrderBy(c => c.CurrencyName),
                "name_desc" => query.OrderByDescending(c => c.CurrencyName),
                _           => query.OrderBy(c => c.CurrencyCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Currency>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Currency/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Currency { IsActive = true };
            if (IsAjax) return PartialView("_CurrencyForm", model);
            return View("_CurrencyForm", model);
        }

        // POST: /Currency/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Currency.CurrencyCode), nameof(Currency.CurrencyName), nameof(Currency.ShortName),
            nameof(Currency.Symbol), nameof(Currency.DecimalPlaces), nameof(Currency.IsActive))] Currency model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_CurrencyForm", model);
                return View("_CurrencyForm", model);
            }

            var exists = await _context.Currencies.AnyAsync(c => c.CurrencyCode == model.CurrencyCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.CurrencyCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_CurrencyForm", model);
                return View("_CurrencyForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            _context.Currencies.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            _currencyService.InvalidateCache();
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Currency/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Currencies.FindAsync(code);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_CurrencyForm", rec);
            return View("_CurrencyForm", rec);
        }

        // POST: /Currency/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Currency updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_CurrencyForm", updated);
                return View("_CurrencyForm", updated);
            }

            var rec = await _context.Currencies.FindAsync(code);
            if (rec == null) return NotFound();

            rec.CurrencyName  = updated.CurrencyName;
            rec.ShortName     = updated.ShortName;
            rec.Symbol        = updated.Symbol;
            rec.DecimalPlaces = updated.DecimalPlaces;
            rec.IsActive      = updated.IsActive;
            rec.UpdatedAt     = DateTime.UtcNow;

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            _currencyService.InvalidateCache();
            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Currency/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Currencies.FindAsync(code);
            if (rec != null)
            {
                // 本幣設定與各單據上的 CurrencyCode 皆為鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免刪除後單據幣別懸空。
                if (await IsCurrencyInUseAsync(code))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CurrencyInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["CurrencyInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.Currencies.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                    return StatusCode(500);
                }
                _currencyService.InvalidateCache();
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>幣別是否為本幣設定，或仍被各模組單據（AP/AR/採購/銷售/委外/銀行帳戶等）引用中；跨廠檢查。</summary>
        private async Task<bool> IsCurrencyInUseAsync(string code)
        {
            if (await _currencyService.GetBaseCurrencyAsync() == code)
                return true;

            if (await _context.CurrencyRates.AnyAsync(r => r.CurrencyCode == code))
                return true;

            if (await _context.BankAccounts.IgnoreQueryFilters().AnyAsync(a => a.CurrencyCode == code))
                return true;

            if (await _context.PurchaseOrderLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.GoodsReceiptLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.GoodsReturnLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.VendorPriceLists.IgnoreQueryFilters().AnyAsync(v => v.CurrencyCode == code))
                return true;

            if (await _context.SalesOrderLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.GoodsShipmentLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.SalesReturnLines.AnyAsync(l => l.CurrencyCode == code))
                return true;

            if (await _context.OutsourceQuotations.IgnoreQueryFilters().AnyAsync(o => o.CurrencyCode == code))
                return true;
            if (await _context.OutsourcePurchaseOrderLines.AnyAsync(l => l.CurrencyCode == code))
                return true;
            if (await _context.OutsourceReceipts.IgnoreQueryFilters().AnyAsync(o => o.CurrencyCode == code))
                return true;
            if (await _context.OutsourceReceiptLines.AnyAsync(l => l.CurrencyCode == code))
                return true;

            if (await _context.LandedCostAllocations.IgnoreQueryFilters().AnyAsync(l => l.CurrencyCode == code))
                return true;

            if (await _context.APInvoices.IgnoreQueryFilters().AnyAsync(i => i.CurrencyCode == code))
                return true;
            if (await _context.APPayments.IgnoreQueryFilters().AnyAsync(p => p.CurrencyCode == code))
                return true;
            if (await _context.ARInvoices.IgnoreQueryFilters().AnyAsync(i => i.CurrencyCode == code))
                return true;
            if (await _context.ARReceipts.IgnoreQueryFilters().AnyAsync(r => r.CurrencyCode == code))
                return true;
            if (await _context.JournalLines.AnyAsync(j => j.CurrencyCode == code))
                return true;
            if (await _context.FxRevaluationEntries.IgnoreQueryFilters().AnyAsync(f => f.CurrencyCode == code))
                return true;

            return false;
        }

        // GET: /Currency/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Currencies.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.CurrencyCode.ToLower().Contains(lower) ||
                    c.CurrencyName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(c => c.CurrencyCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Currency.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["CurrencyCode"].Value, L["CurrencyName"].Value, L["ShortName"].Value,
                L["Symbol"].Value, L["DecimalPlaces"].Value, L["IsActive"].Value
            };
            var rows = data.Select(c => new object?[]
            {
                c.CurrencyCode, c.CurrencyName, c.ShortName, c.Symbol, c.DecimalPlaces, c.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Currency" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Currency.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Currency.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Currency.json"),
            };
        }

        // AJAX: GET /Currency/GetRates?currencyCode=xxx
        [HttpGet]
        public async Task<IActionResult> GetRates(string currencyCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var list = await _context.CurrencyRates
                .AsNoTracking()
                .Where(r => r.CurrencyCode == currencyCode)
                .OrderByDescending(r => r.RateDate)
                .ToListAsync();

            var result = list.Select(r => new {
                r.CurrencyCode,
                RateDate = r.RateDate.ToString("yyyy-MM-dd"),
                r.BuyRate,
                r.SellRate
            });
            return Json(result);
        }

        // AJAX: POST /Currency/SaveRate
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRate(string currencyCode, string rateDate, decimal buyRate, decimal sellRate)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(currencyCode) || string.IsNullOrWhiteSpace(rateDate))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            if (!DateOnly.TryParseExact(rateDate, "yyyy-MM-dd", out var date))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            var existing = await _context.CurrencyRates.FindAsync(currencyCode, date);
            if (existing != null)
            {
                existing.BuyRate  = buyRate;
                existing.SellRate = sellRate;
            }
            else
            {
                _context.CurrencyRates.Add(new CurrencyRate
                {
                    CurrencyCode = currencyCode,
                    RateDate     = date,
                    BuyRate      = buyRate,
                    SellRate     = sellRate
                });
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["SaveFailed"].Value });
            }

            return Json(new { success = true, message = _localizer["SaveSuccess"].Value });
        }

        // AJAX: POST /Currency/DeleteRate
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRate(string currencyCode, string rateDate)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            if (!DateOnly.TryParseExact(rateDate, "yyyy-MM-dd", out var date))
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            var rec = await _context.CurrencyRates.FindAsync(currencyCode, date);
            if (rec != null)
            {
                _context.CurrencyRates.Remove(rec);
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
