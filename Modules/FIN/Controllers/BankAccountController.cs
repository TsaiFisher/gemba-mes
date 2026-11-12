using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Controllers
{
    [Authorize]
    public class BankAccountController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "BankAccount";

        public BankAccountController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDocNumberService docNo, IDateTimeService dt,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _docNo = docNo; _dt = dt;
            _currencyService = currencyService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(string? search, string? sortOrder, bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.SearchTerm      = search;
            ViewBag.CanCreate       = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate       = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete       = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<BankAccount>?)null);

            var query = _context.BankAccounts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(b => b.AccountId.Contains(search) || b.BankName.Contains(search));

            var list = await PaginatedList<BankAccount>.CreateAsync(
                query.OrderBy(b => b.AccountId), pageNumber ?? 1, pageSize ?? 20);
            return View(list);
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdownsAsync();
            var vm = new BankAccount { PlantId = _userContext.PlantId };
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(BankAccount vm)
        {
            if (!ModelState.IsValid)
                return PartialView("_Form", vm);

            // AccountId 是「使用者輸入」的主鍵，新增時同樣有值，因此不能用
            // string.IsNullOrEmpty(vm.AccountId) 判斷新舊——那會讓新增永遠被判為編輯，
            // 走下方分支後 FindAsync 找不到而回 404，「新增銀行帳戶」永遠建不出資料。
            // 以資料庫是否已存在該筆為準才是正確的判別。
            var existing = await _context.BankAccounts.FindAsync(vm.AccountId);
            if (existing == null)
            {
                if (!await _permissionService.CanCreate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });
                vm.PlantId   = _userContext.PlantId;
                vm.CreatedAt = DateTime.UtcNow;
                vm.UpdatedAt = DateTime.UtcNow;
                _context.BankAccounts.Add(vm);
            }
            else
            {
                if (!await _permissionService.CanUpdate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
                existing.BankName    = vm.BankName;
                existing.AccountNo   = vm.AccountNo;
                existing.CurrencyCode = vm.CurrencyCode;
                existing.IsActive    = vm.IsActive;
                existing.Remarks     = vm.Remarks;
                existing.UpdatedAt   = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            await PrepareFormDropdownsAsync();
            var vm = await _context.BankAccounts.FindAsync(id);
            if (vm == null) return NotFound();
            if (IsAjax) return PartialView("_Form", vm);
            return View("_Form", vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoDeletePermission"].Value });
            var vm = await _context.BankAccounts.FindAsync(id);
            if (vm == null) return NotFound();
            _context.BankAccounts.Remove(vm);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        private async Task PrepareFormDropdownsAsync()
        {
            ViewBag.CurrencyOptions = await _context.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { Code = c.CurrencyCode, Name = $"{c.CurrencyCode} — {c.CurrencyName}" })
                .ToListAsync();
        }
    }
}
