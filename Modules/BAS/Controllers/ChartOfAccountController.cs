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
    /// <summary>
    /// 會計科目維護（全公司共用，不分廠）。
    ///
    /// <para>系統科目（<c>IsSystem</c>，由 <c>StandardAccounts</c> 種子寫入）**代碼與性質不可修改、
    /// 不可刪除**——自動分錄會用到它們，改了之後傳票會落在錯誤的報表段落，而且不會有任何錯誤訊息。
    /// 使用者能改的只有名稱、英文名稱、備註、可否手動選用與是否啟用。</para>
    ///
    /// <para>詳見 <see cref="ChartOfAccount"/> 的類別註解（含由 <c>InvoiceAccount</c> 升級的沿革）。</para>
    /// </summary>
    [Authorize]
    public class ChartOfAccountController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ChartOfAccount";

        public ChartOfAccountController(ApplicationDbContext context,
            IExportService exportService, IPermissionService permissionService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _exportService = exportService;
            _permissionService = permissionService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }

        // GET: /ChartOfAccount
        public async Task<IActionResult> Index(
            string? searchTerm, string? searchType,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm;
            ViewBag.SearchType = searchType;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CodeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<ChartOfAccount>?)null);

            var query = BuildQuery(searchTerm, searchType);

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(a => a.AccountCode),
                "name_asc"  => query.OrderBy(a => a.AccountName),
                "name_desc" => query.OrderByDescending(a => a.AccountName),
                _           => query.OrderBy(a => a.AccountCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<ChartOfAccount>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /ChartOfAccount/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new ChartOfAccount { IsActive = true };
            if (IsAjax) return PartialView("_ChartOfAccountForm", model);
            return View("_ChartOfAccountForm", model);
        }

        // POST: /ChartOfAccount/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(ChartOfAccount.AccountCode), nameof(ChartOfAccount.AccountName),
            nameof(ChartOfAccount.AccountNameEn), nameof(ChartOfAccount.AccountType),
            nameof(ChartOfAccount.Remarks), nameof(ChartOfAccount.AllowManualEntry),
            nameof(ChartOfAccount.IsActive))] ChartOfAccount model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ChartOfAccountForm", model);
                return View("_ChartOfAccountForm", model);
            }

            var exists = await _context.Set<ChartOfAccount>().AnyAsync(a => a.AccountCode == model.AccountCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.AccountCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ChartOfAccountForm", model);
                return View("_ChartOfAccountForm", model);
            }

            // IsSystem 不在 Bind 清單裡，這裡再明確歸零一次：
            // 使用者自建的科目永遠不是系統科目，否則它會取得「不可刪」的保護卻沒有自動分錄在用。
            model.IsSystem  = false;
            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.Set<ChartOfAccount>().Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /ChartOfAccount/Edit/{id}
        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Set<ChartOfAccount>().FindAsync(id);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ChartOfAccountForm", rec);
            return View("_ChartOfAccountForm", rec);
        }

        // POST: /ChartOfAccount/Edit/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, ChartOfAccount updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ChartOfAccountForm", updated);
                return View("_ChartOfAccountForm", updated);
            }

            var rec = await _context.Set<ChartOfAccount>().FindAsync(id);
            if (rec == null) return NotFound();

            // AccountCode 是主鍵也是分錄上留下的痕跡，不可改：既有傳票的 AccountCode 是
            // 當下寫進去的字串，改了代碼會讓歷史傳票指向一個不存在的科目。
            rec.AccountName   = updated.AccountName;
            rec.AccountNameEn = updated.AccountNameEn;
            rec.Remarks       = updated.Remarks;
            rec.IsActive      = updated.IsActive;
            rec.AllowManualEntry = updated.AllowManualEntry;

            // 系統科目的性質由 StandardAccounts 決定，表單就算送了也不採用。
            // 畫面已改成唯讀，但唯讀是提示不是閘門——直接 POST 一樣到得了這裡。
            if (!rec.IsSystem)
                rec.AccountType = updated.AccountType;

            rec.UpdatedAt = DateTime.UtcNow;

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

        // POST: /ChartOfAccount/Delete/{id}
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Set<ChartOfAccount>().FindAsync(id);
            if (rec == null)
            {
                if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            // 系統科目不可刪：自動分錄隨時會用到它，刪掉之後下一筆過帳產生的傳票
            // 會指向一個查不到的科目，而過帳那條路上沒有任何地方會檢查科目存不存在。
            if (rec.IsSystem)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedSystemAccount"].Value });
                return StatusCode(409);
            }

            // 被發票或傳票引用過就不准刪，只能停用。刪掉之後那些明細的 AccountCode 會指向
            // 一個查不到的代碼，而**作廢回沖是靠對調原分錄**，不重算科目——所以帳面不會壞，
            // 壞的是「這筆費用當初記到哪個科目」再也查不出來。沒有 FK 擋，得自己查。
            var referenced =
                await _context.Set<Modules.FIN.Models.APInvoiceLine>().AnyAsync(l => l.AccountCode == id)
             || await _context.Set<Modules.FIN.Models.ARInvoiceLine>().AnyAsync(l => l.AccountCode == id)
             || await _context.Set<Modules.ACT.Models.JournalLine>().AnyAsync(l => l.AccountCode == id);
            if (referenced)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(409);
            }

            _context.Set<ChartOfAccount>().Remove(rec);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /ChartOfAccount/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null, string? searchType = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var data = await BuildQuery(searchTerm, searchType).OrderBy(a => a.AccountCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.ChartOfAccount.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["AccountCode"].Value, L["AccountName"].Value, L["AccountType"].Value,
                L["IsSystem"].Value, L["AllowManualEntry"].Value,
                L["IsActive"].Value, L["Remarks"].Value
            };
            var rows = data.Select(a => new object?[]
            {
                a.AccountCode, a.AccountName, L["AccountType_" + a.AccountType].Value,
                a.IsSystem, a.AllowManualEntry, a.IsActive, a.Remarks
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "ChartOfAccount" };
            return format?.ToLower() switch
            {
                "xlsx" => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "ChartOfAccount.xlsx"),
                "csv"  => File(_exportService.ToCsv(req), "text/csv", "ChartOfAccount.csv"),
                _      => File(_exportService.ToJson(req), "application/json", "ChartOfAccount.json"),
            };
        }

        /// <summary>列表與匯出共用同一段條件——兩邊各寫一次就會慢慢分岔，而匯出很少有人核對。</summary>
        private IQueryable<ChartOfAccount> BuildQuery(string? searchTerm, string? searchType)
        {
            var query = _context.Set<ChartOfAccount>().AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(a =>
                    a.AccountCode.ToLower().Contains(lower) ||
                    a.AccountName.ToLower().Contains(lower));
            }

            if (Enum.TryParse<AccountType>(searchType, out var type))
                query = query.Where(a => a.AccountType == type);

            return query;
        }
    }
}
