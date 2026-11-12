using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.Json;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class ShiftGroupController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private const string MenuKey = "ShiftGroupMaster";

        public ShiftGroupController(ApplicationDbContext context, IApprovalService approvalService,
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


        // GET: /ShiftGroup
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

            // 班別主檔（供每日排班的班次下拉與工時自動推算）
            ViewBag.ShiftsJson = await BuildShiftsJsonAsync();

            if (!searched) return View((PaginatedList<ShiftGroup>?)null);

            var query = _context.ShiftGroups.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(g =>
                    g.GroupCode.ToLower().Contains(lower) ||
                    g.GroupName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(g => g.GroupCode),
                "name_asc"  => query.OrderBy(g => g.GroupName),
                "name_desc" => query.OrderByDescending(g => g.GroupName),
                _           => query.OrderBy(g => g.GroupCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<ShiftGroup>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /ShiftGroup/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new ShiftGroup { IsActive = true };
            if (IsAjax) return PartialView("_ShiftGroupForm", model);
            return View("_ShiftGroupForm", model);
        }

        // POST: /ShiftGroup/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(ShiftGroup.GroupCode), nameof(ShiftGroup.GroupName),
            nameof(ShiftGroup.IsDefault), nameof(ShiftGroup.IsActive))] ShiftGroup model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ShiftGroupForm", model);
                return View("_ShiftGroupForm", model);
            }

            var exists = await _context.ShiftGroups.AnyAsync(g => g.GroupCode == model.GroupCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.GroupCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ShiftGroupForm", model);
                return View("_ShiftGroupForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            // 每廠至多一筆預設班制：設為預設時清除本廠其他預設（全域查詢過濾器已限本廠）
            if (model.IsDefault)
                await ClearOtherDefaultsAsync(model.GroupCode);

            _context.ShiftGroups.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }

            // Auto-initialize 7 day rows
            await InitializeDaysIfNeeded(model.GroupCode);

            // 回傳 groupCode：前端據此新增後直接切入「每日排班」頁簽，省去再點編輯
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value, groupCode = model.GroupCode });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /ShiftGroup/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.ShiftGroups.FindAsync(code);
            if (rec == null) return NotFound();

            // Auto-initialize 7 day rows if not present
            await InitializeDaysIfNeeded(code);

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ShiftGroupForm", rec);
            return View("_ShiftGroupForm", rec);
        }

        // POST: /ShiftGroup/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, ShiftGroup updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ShiftGroupForm", updated);
                return View("_ShiftGroupForm", updated);
            }

            var rec = await _context.ShiftGroups.FindAsync(code);
            if (rec == null) return NotFound();

            rec.GroupName = updated.GroupName;
            rec.IsActive  = updated.IsActive;
            rec.IsDefault = updated.IsDefault;
            rec.UpdatedAt = DateTime.UtcNow;

            // 每廠至多一筆預設班制
            if (updated.IsDefault)
                await ClearOtherDefaultsAsync(code);

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

        // POST: /ShiftGroup/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.ShiftGroups.FindAsync(code);
            if (rec != null)
            {
                // 工作中心預設班制、工作曆展開來源皆以 ShiftGroupCode 鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免班制刪除後留下孤兒設定。
                if (await IsShiftGroupInUseAsync(code))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["ShiftGroupInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["ShiftGroupInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.ShiftGroups.Remove(rec);
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

        /// <summary>班制是否仍被工作中心預設班制或工作曆引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsShiftGroupInUseAsync(string code)
        {
            if (await _context.WorkCenters.IgnoreQueryFilters().AnyAsync(w => w.ShiftGroupCode == code))
                return true;
            if (await _context.WorkCalendars.IgnoreQueryFilters().AnyAsync(w => w.ShiftGroupCode == code))
                return true;
            return false;
        }

        // GET: /ShiftGroup/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.ShiftGroups.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(g =>
                    g.GroupCode.ToLower().Contains(lower) ||
                    g.GroupName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(g => g.GroupCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.ShiftGroup.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["GroupCode"].Value, L["GroupName"].Value, L["IsActive"].Value
            };
            var rows = data.Select(g => new object?[]
            {
                g.GroupCode, g.GroupName, g.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "ShiftGroup" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "ShiftGroup.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "ShiftGroup.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "ShiftGroup.json"),
            };
        }

        // AJAX: GET /ShiftGroup/GetDays?groupCode=xxx
        [HttpGet]
        public async Task<IActionResult> GetDays(string groupCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var list = await _context.ShiftGroupDays
                .AsNoTracking()
                .Where(d => d.GroupCode == groupCode)
                .OrderBy(d => d.DayOfWeek)
                .ToListAsync();

            var result = list.Select(d => new {
                d.GroupCode,
                d.DayOfWeek,
                d.Shift1Code,
                d.Shift2Code,
                d.Shift3Code,
                d.Shift4Code,
                WorkStart          = d.WorkStart?.ToString("HH:mm"),
                WorkEnd            = d.WorkEnd?.ToString("HH:mm"),
                d.DailyRegularHours,
                d.DailyOvertimeHours
            });
            return Json(result);
        }

        // AJAX: POST /ShiftGroup/SaveDay
        // 正班工時/開班/收班一律由所選班別（班次）伺服端推算（單一真相來源）；
        // 加班工時維持手動填入。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDay(string groupCode, int dayOfWeek,
            string? shift1Code, string? shift2Code, string? shift3Code, string? shift4Code,
            decimal dailyOvertimeHours = 0)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(groupCode) || dayOfWeek < 1 || dayOfWeek > 7)
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            // 由所選班別推算：正班工時 = 各班別淨工時加總；開班 = 最早 StartTime；收班 = 最晚 EndTime
            var codes = new[] { shift1Code, shift2Code, shift3Code, shift4Code }
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim())
                .Distinct()
                .ToList();

            var shifts = codes.Count == 0
                ? new List<Shift>()
                : await _context.Shifts.AsNoTracking().Where(s => codes.Contains(s.ShiftCode)).ToListAsync();
            var (regularHours, tsStart, tsEnd) = ComputeDayTotals(shifts);

            var existing = await _context.ShiftGroupDays.FindAsync(groupCode, dayOfWeek);
            if (existing != null)
            {
                existing.Shift1Code          = shift1Code;
                existing.Shift2Code          = shift2Code;
                existing.Shift3Code          = shift3Code;
                existing.Shift4Code          = shift4Code;
                existing.WorkStart           = tsStart;
                existing.WorkEnd             = tsEnd;
                existing.DailyRegularHours   = regularHours;
                existing.DailyOvertimeHours  = dailyOvertimeHours;
            }
            else
            {
                _context.ShiftGroupDays.Add(new ShiftGroupDay
                {
                    GroupCode           = groupCode,
                    DayOfWeek           = dayOfWeek,
                    Shift1Code          = shift1Code,
                    Shift2Code          = shift2Code,
                    Shift3Code          = shift3Code,
                    Shift4Code          = shift4Code,
                    WorkStart           = tsStart,
                    WorkEnd             = tsEnd,
                    DailyRegularHours   = regularHours,
                    DailyOvertimeHours  = dailyOvertimeHours
                });
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["SaveFailed"].Value });
            }

            return Json(new
            {
                success = true,
                message = _localizer["SaveSuccess"].Value,
                dailyRegularHours = regularHours,
                workStart = tsStart?.ToString("HH:mm"),
                workEnd = tsEnd?.ToString("HH:mm")
            });
        }

        // AJAX: POST /ShiftGroup/SaveDays — 整週一次儲存（JSON body）
        // 與 SaveDay 相同規則：正班工時/開班/收班由所選班別伺服端推算；加班手填。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDays([FromBody] SaveDaysRequest request)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (request == null || string.IsNullOrWhiteSpace(request.GroupCode) ||
                request.Days == null || request.Days.Count == 0)
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            var days = request.Days.Where(d => d.DayOfWeek is >= 1 and <= 7).ToList();
            if (days.Count == 0)
                return Json(new { success = false, message = _localizer["OperationFailed"].Value });

            // 一次預載本批所有用到的班別，避免逐天查詢
            var allCodes = days
                .SelectMany(d => new[] { d.Shift1Code, d.Shift2Code, d.Shift3Code, d.Shift4Code })
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim())
                .Distinct()
                .ToList();
            var shiftMap = allCodes.Count == 0
                ? new Dictionary<string, Shift>()
                : await _context.Shifts.AsNoTracking()
                    .Where(s => allCodes.Contains(s.ShiftCode))
                    .ToDictionaryAsync(s => s.ShiftCode);

            var existingDays = await _context.ShiftGroupDays
                .Where(d => d.GroupCode == request.GroupCode)
                .ToListAsync();

            var results = new List<object>();
            foreach (var d in days)
            {
                var dayShifts = new[] { d.Shift1Code, d.Shift2Code, d.Shift3Code, d.Shift4Code }
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!.Trim())
                    .Distinct()
                    .Where(shiftMap.ContainsKey)
                    .Select(c => shiftMap[c])
                    .ToList();
                var (regularHours, tsStart, tsEnd) = ComputeDayTotals(dayShifts);

                var existing = existingDays.FirstOrDefault(x => x.DayOfWeek == d.DayOfWeek);
                if (existing != null)
                {
                    existing.Shift1Code         = d.Shift1Code;
                    existing.Shift2Code         = d.Shift2Code;
                    existing.Shift3Code         = d.Shift3Code;
                    existing.Shift4Code         = d.Shift4Code;
                    existing.WorkStart          = tsStart;
                    existing.WorkEnd            = tsEnd;
                    existing.DailyRegularHours  = regularHours;
                    existing.DailyOvertimeHours = d.DailyOvertimeHours;
                }
                else
                {
                    _context.ShiftGroupDays.Add(new ShiftGroupDay
                    {
                        GroupCode           = request.GroupCode,
                        DayOfWeek           = d.DayOfWeek,
                        Shift1Code          = d.Shift1Code,
                        Shift2Code          = d.Shift2Code,
                        Shift3Code          = d.Shift3Code,
                        Shift4Code          = d.Shift4Code,
                        WorkStart           = tsStart,
                        WorkEnd             = tsEnd,
                        DailyRegularHours   = regularHours,
                        DailyOvertimeHours  = d.DailyOvertimeHours
                    });
                }

                results.Add(new
                {
                    dayOfWeek = d.DayOfWeek,
                    dailyRegularHours = regularHours,
                    workStart = tsStart?.ToString("HH:mm"),
                    workEnd = tsEnd?.ToString("HH:mm")
                });
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = _localizer["SaveFailed"].Value });
            }

            return Json(new { success = true, message = _localizer["SaveSuccess"].Value, days = results });
        }

        // 由所選班別推算當日：正班工時 = 各班別淨工時加總；開班 = 最早 StartTime；收班 = 最晚 EndTime
        private static (decimal regularHours, TimeOnly? start, TimeOnly? end) ComputeDayTotals(IEnumerable<Shift> shifts)
        {
            decimal regularHours = 0;
            TimeOnly? start = null, end = null;
            foreach (var s in shifts)
            {
                regularHours += s.WorkMinutes ?? 0;
                if (s.StartTime is { } st && (start == null || st < start)) start = st;
                if (s.EndTime   is { } en && (end   == null || en > end))   end   = en;
            }
            return (Math.Round(regularHours / 60m, 2), start, end);
        }

        private async Task<string> BuildShiftsJsonAsync()
        {
            var shifts = await _context.Shifts.AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.ShiftCode)
                .Select(s => new { s.ShiftCode, s.ShiftName, s.StartTime, s.EndTime, s.WorkMinutes })
                .ToListAsync();

            return JsonSerializer.Serialize(shifts.Select(s => new
            {
                code = s.ShiftCode,
                name = s.ShiftName,
                start = s.StartTime?.ToString("HH:mm"),
                end = s.EndTime?.ToString("HH:mm"),
                mins = s.WorkMinutes ?? 0
            }));
        }

        // 清除本廠其他班制的預設旗標（保留 keepGroupCode）。ShiftGroup 為 IPlantEntity，查詢已自動限本廠。
        private async Task ClearOtherDefaultsAsync(string keepGroupCode)
        {
            var others = await _context.ShiftGroups
                .Where(g => g.IsDefault && g.GroupCode != keepGroupCode)
                .ToListAsync();
            foreach (var g in others) g.IsDefault = false;
        }

        private async Task InitializeDaysIfNeeded(string groupCode)
        {
            var existingCount = await _context.ShiftGroupDays.CountAsync(d => d.GroupCode == groupCode);
            if (existingCount >= 7) return;

            var existingDays = await _context.ShiftGroupDays
                .Where(d => d.GroupCode == groupCode)
                .Select(d => d.DayOfWeek)
                .ToListAsync();

            for (int day = 1; day <= 7; day++)
            {
                if (!existingDays.Contains(day))
                {
                    _context.ShiftGroupDays.Add(new ShiftGroupDay
                    {
                        GroupCode = groupCode,
                        DayOfWeek = day
                    });
                }
            }
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>整週排班批次儲存的請求模型（JSON body）。</summary>
    public class SaveDaysRequest
    {
        public string GroupCode { get; set; } = string.Empty;
        public List<SaveDayInput> Days { get; set; } = new();
    }

    public class SaveDayInput
    {
        public int DayOfWeek { get; set; }
        public string? Shift1Code { get; set; }
        public string? Shift2Code { get; set; }
        public string? Shift3Code { get; set; }
        public string? Shift4Code { get; set; }
        public decimal DailyOvertimeHours { get; set; }
    }
}
