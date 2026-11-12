using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Controllers
{
    [Authorize]
    public class AccountingPeriodController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IAccountingPeriodService _periodService;
        private readonly IMonthEndService _monthEndService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "AccountingPeriod";

        public AccountingPeriodController(
            ApplicationDbContext context,
            IPermissionService permissionService,
            IUserContext userContext,
            IAccountingPeriodService periodService,
            IMonthEndService monthEndService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _permissionService = permissionService;
            _userContext = userContext;
            _periodService = periodService;
            _monthEndService = monthEndService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(
            bool searched = false, int? year = null, PeriodStatus? statusFilter = null,
            int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Year            = year;
            ViewBag.StatusFilter    = statusFilter;
            // 年度下拉只列真的有資料的年份——列一串空年份只會讓使用者以為查壞了。
            ViewBag.YearOptions     = await _context.AccountingPeriods.AsNoTracking()
                .Select(p => p.Year).Distinct().OrderByDescending(y => y).ToListAsync();
            ViewBag.CanCreate       = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate       = await _permissionService.CanUpdate(MenuKey);

            if (!searched) return View((PaginatedList<AccountingPeriod>?)null);

            // 不加 IgnoreQueryFilters：期間是 IPlantEntity，這裡要的就是「只看本廠」。
            var query = _context.AccountingPeriods.AsQueryable();
            if (year is > 0)         query = query.Where(p => p.Year == year);
            if (statusFilter != null) query = query.Where(p => p.Status == statusFilter);

            var page = await PaginatedList<AccountingPeriod>.CreateAsync(
                query.OrderByDescending(p => p.PeriodId).AsNoTracking(), pageNumber ?? 1, pageSize ?? 20);
            ViewBag.UserNames = await BuildUserNamesAsync(page.Select(p => p.ClosedBy));
            return View(page);
        }

        /// <summary>UserId → UserName 對照表（畫面顯示 ClosedBy 用，避免直接印出 GUID）。</summary>
        private async Task<Dictionary<string, string>> BuildUserNamesAsync(IEnumerable<string?> userIds)
        {
            var ids = userIds.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
            return ids.Count == 0
                ? new Dictionary<string, string>()
                : await _context.Users.AsNoTracking()
                    .Where(u => ids.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
        }

        // POST：建立下一期間
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateNext()
        {
            if (!await _permissionService.CanCreate(MenuKey))
                return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });
            try
            {
                var p = await _periodService.CreateNextPeriodAsync(_userContext.PlantId ?? "");
                return Json(new { success = true, message = string.Format(_localizer["PeriodCreated"].Value, p.PeriodId) });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // POST：開始月結（Open → Closing）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> StartClosing(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _periodService.StartClosingAsync(id, _userContext.PlantId ?? "", _userContext.UserId ?? "");
                return Json(new { success = true, message = string.Format(_localizer["PeriodStartedClosing"].Value, id) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：關帳（Closing → Closed）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Close(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _periodService.ClosePeriodAsync(id, _userContext.PlantId ?? "", _userContext.UserId ?? "");
                return Json(new { success = true, message = string.Format(_localizer["PeriodClosed"].Value, id) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：取消月結（Closing → Open／Reopened）
        //
        // 這顆鈕原本只長在「月結作業」畫面、而且只有在清單步驟 8 為「已完成」時才出現。
        // 但期間可以從本畫面的〈開始月結〉直接進 Closing，那時不但沒有清單，
        // 之後補建清單再按步驟 8 也會因為「狀態為 Closing，無法開始月結」而拋錯——
        // 於是那顆鈕永遠不會出現。實測（2026-09-06、P2 廠 202609）確認：
        // 進了 Closing 之後 UI 上唯一的出口是〈關帳〉，而關帳要稽核全過；
        // 稽核沒過就完全卡死，只能直接改資料庫。故在按下〈開始月結〉的同一個畫面補上回頭路。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelClosing(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _monthEndService.CancelClosingByPeriodAsync(
                    id, _userContext.PlantId ?? "", _userContext.UserId ?? "");
                return Json(new { success = true, message = _localizer["ClosingCancelled"].Value });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：重開帳（Closed → Reopened）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reopen(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _periodService.ReopenPeriodAsync(id, _userContext.PlantId ?? "", _userContext.UserId ?? "");
                return Json(new { success = true, message = string.Format(_localizer["PeriodReopened"].Value, id) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // GET：取得期間備註（供 Side Panel 查看）
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            // 複合主鍵 (PeriodId, PlantId)：FindAsync 只給 id 會編譯得過但查不到東西。
            var p = await _context.AccountingPeriods
                .FindAsync(new object?[] { id, _userContext.PlantId });
            if (p == null) return NotFound();
            ViewBag.IsReadOnly = true;
            ViewBag.UserNames = await BuildUserNamesAsync(new[] { p.ClosedBy });
            if (IsAjax) return PartialView("_Form", p);
            return View("_Form", p);
        }
    }
}
