using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using System.Text.Json;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class WorkCalendarController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public WorkCalendarController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, DateOnly? fromDate = null, DateOnly? toDate = null, string? workCenterId = null, bool searched = false, int pageSize = 20)
        {
            if (!await _permissionService.CanRead("WorkCalendar")) return Forbid();

            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");
            ViewBag.WorkCenterId = workCenterId;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;
            ViewBag.WorkCenters = await _context.Set<WorkCenter>()
                .AsNoTracking().OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            await SetPermissionViewBagAsync("WorkCalendar");
            if (!searched) return View((PaginatedList<WorkCalendar>?)null);

            var query = _context.Set<WorkCalendar>().AsNoTracking();

            if (fromDate.HasValue)
                query = query.Where(wc => wc.Date >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(wc => wc.Date <= toDate.Value);

            if (!string.IsNullOrEmpty(workCenterId))
                query = query.Where(wc => wc.WorkCenterId == workCenterId);

            query = query.OrderByDescending(wc => wc.Date).ThenBy(wc => wc.WorkCenterId).ThenBy(wc => wc.MachineCode);

            var list = await PaginatedList<WorkCalendar>.CreateAsync(query, pageNumber ?? 1, pageSize);

            return View(list);
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate("WorkCalendar")) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new WorkCalendar()) : View("_Form", new WorkCalendar());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(WorkCalendar workCalendar)
        {
            if (!await _permissionService.CanCreate("WorkCalendar"))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoCreatePermission"].Value)});</script>", "text/html") : Forbid();

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return IsAjax ? PartialView("_Form", workCalendar) : View("_Form", workCalendar);
            }

            workCalendar.PlantId ??= _userContext.PlantId;
            // 正規化為 null（空字串會與唯一索引/三層查詢的 IS NULL 語意不符）
            workCalendar.WorkCenterId = string.IsNullOrWhiteSpace(workCalendar.WorkCenterId) ? null : workCalendar.WorkCenterId;
            workCalendar.MachineCode  = string.IsNullOrWhiteSpace(workCalendar.MachineCode)  ? null : workCalendar.MachineCode;

            // 重複檢查鍵對齊唯一索引 (PlantId, WorkCenterId, MachineCode, Date)，不含 ShiftGroupCode
            // （ShiftGroupCode 僅為「以哪個班制展開」的來源註記，非身份鍵；含它會與唯一索引衝突）
            var existing = await _context.Set<WorkCalendar>().FirstOrDefaultAsync(wc =>
                wc.PlantId == workCalendar.PlantId &&
                wc.WorkCenterId == workCalendar.WorkCenterId &&
                wc.MachineCode == workCalendar.MachineCode &&
                wc.Date == workCalendar.Date);

            if (existing != null)
            {
                ModelState.AddModelError("", _localizer["DuplicateRecord"].Value);
                await PrepareFormDropdowns();
                return IsAjax ? PartialView("_Form", workCalendar) : View("_Form", workCalendar);
            }

            _context.Set<WorkCalendar>().Add(workCalendar);
            await _context.SaveChangesAsync();

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["CreateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        public async Task<IActionResult> Edit(int id)
        {
            if (!await _permissionService.CanUpdate("WorkCalendar")) return Forbid();
            var workCalendar = await _context.Set<WorkCalendar>().FindAsync(id);
            if (workCalendar == null) return NotFound();

            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", workCalendar) : View("_Form", workCalendar);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, WorkCalendar workCalendar)
        {
            if (!await _permissionService.CanUpdate("WorkCalendar"))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoUpdatePermission"].Value)});</script>", "text/html") : Forbid();

            if (id != workCalendar.Id) return NotFound();

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return IsAjax ? PartialView("_Form", workCalendar) : View("_Form", workCalendar);
            }

            var existing = await _context.Set<WorkCalendar>().FindAsync(id);
            if (existing == null) return NotFound();

            // 先記下影響「可用時段」的舊值：改完要不要重建時段，看的是這幾個欄位有沒有動。
            var before = (existing.ShiftGroupCode, existing.WorkCenterId, existing.MachineCode,
                          existing.Date, existing.TM_WKO);

            existing.ShiftGroupCode = workCalendar.ShiftGroupCode;
            existing.WorkCenterId   = workCalendar.WorkCenterId;
            existing.MachineCode    = workCalendar.MachineCode;
            existing.Date           = workCalendar.Date;
            existing.WorkStart      = workCalendar.WorkStart;
            existing.WorkEnd        = workCalendar.WorkEnd;
            existing.TM_WKS         = workCalendar.TM_WKS;
            existing.TM_WKO         = workCalendar.TM_WKO;
            existing.Note           = workCalendar.Note;

            _context.Update(existing);
            await _context.SaveChangesAsync();

            // 可用時段自 2026-08-26 起含加班（TM_WKO），所以改了加班時數就必須重建時段，
            // 否則排程吃不到這次修改——而且**不會有任何錯誤訊息**：存檔成功、排程結果照舊。
            // 日期或範疇（班制／工作中心／機台）搬動時，新舊兩邊的時段都要重建。
            var after = (existing.ShiftGroupCode, existing.WorkCenterId, existing.MachineCode,
                         existing.Date, existing.TM_WKO);
            if (before != after)
            {
                await GenerateAsync(after.Date.Year, after.Date.Month, after.ShiftGroupCode,
                                    after.WorkCenterId, after.MachineCode, overwrite: false);

                var movedScope = (before.ShiftGroupCode, before.WorkCenterId, before.MachineCode,
                                  before.Date.Year, before.Date.Month)
                              != (after.ShiftGroupCode, after.WorkCenterId, after.MachineCode,
                                  after.Date.Year, after.Date.Month);
                if (movedScope)
                    await GenerateAsync(before.Date.Year, before.Date.Month, before.ShiftGroupCode,
                                        before.WorkCenterId, before.MachineCode, overwrite: false);
            }

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            if (!await _permissionService.CanDelete("WorkCalendar"))
                return IsAjax ? Json(new { success = false, message = _localizer["NoDeletePermission"].Value }) : Forbid();

            var workCalendar = await _context.Set<WorkCalendar>().FindAsync(id);
            if (workCalendar == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            _context.Set<WorkCalendar>().Remove(workCalendar);
            await _context.SaveChangesAsync();

            var message = _localizer["DeleteSuccess"].Value;
            if (IsAjax) return Json(new { success = true, message });
            TempData["SuccessMessage"] = message;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // 取某日排班所用的班別主檔（過濾空碼、查無者略過）
        private static List<Shift> ShiftsOf(ShiftGroupDay sgd, IReadOnlyDictionary<string, Shift> map)
        {
            var list = new List<Shift>();
            foreach (var c in new[] { sgd.Shift1Code, sgd.Shift2Code, sgd.Shift3Code, sgd.Shift4Code })
                if (!string.IsNullOrWhiteSpace(c) && map.TryGetValue(c.Trim(), out var sh)) list.Add(sh);
            return list;
        }

        /// <summary>批次產生工作日曆</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BatchGenerate(
            int year, int? month, string? shiftGroupCode,
            string? workCenterId, string? machineCode, bool overwrite = false)
        {
            if (!await _permissionService.CanCreate("WorkCalendar"))
                return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

            var (ok, message, _) = await GenerateAsync(
                year, month, shiftGroupCode, workCenterId, machineCode, overwrite);
            return Json(new { success = ok, message });
        }

        /// <summary>
        /// 批次展開的本體。**抽成方法是為了讓 <see cref="Edit(int, WorkCalendar)"/> 也能叫它**：
        /// 可用時段（<c>WorkCalendarSegment</c>）自 2026-08-26 起含加班（<c>TM_WKO</c>），
        /// 而加班時數是在這張畫面上逐日改的——改完不重建時段，排程就吃不到那個改動，
        /// 而且**不會有任何錯誤訊息**：使用者把加班從 0 改成 2 小時、按存檔、成功，
        /// 然後排程結果一模一樣。權限檢查留在呼叫端（展開是 Create、編輯是 Update）。
        /// </summary>
        private async Task<(bool Ok, string Message, int Inserted)> GenerateAsync(
            int year, int? month, string? shiftGroupCode,
            string? workCenterId, string? machineCode, bool overwrite)
        {
            var plantId = _userContext.PlantId;
            var wcId = string.IsNullOrWhiteSpace(workCenterId) ? null : workCenterId;
            var mcId = string.IsNullOrWhiteSpace(machineCode)  ? null : machineCode;

            // 班制未指定時 fallback：指定工作中心 → 該 WC 預設班制；否則 → 本廠預設班制（ShiftGroup.IsDefault）。
            // ShiftGroup / WorkCenter 皆為 IPlantEntity，查詢已自動限本廠。
            var effectiveGroup = string.IsNullOrWhiteSpace(shiftGroupCode) ? null : shiftGroupCode;
            if (effectiveGroup == null && wcId != null)
                effectiveGroup = await _context.Set<WorkCenter>()
                    .Where(w => w.WorkCenterCode == wcId)
                    .Select(w => w.ShiftGroupCode)
                    .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(effectiveGroup))
                effectiveGroup = await _context.Set<ShiftGroup>()
                    .Where(g => g.IsDefault)
                    .Select(g => g.GroupCode)
                    .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(effectiveGroup))
                return (false, _localizer["BatchSelectShiftGroup"].Value, 0);

            // 載入班制每日排班（含正班/加班工時）
            var shiftGroupDays = await _context.Set<ShiftGroupDay>()
                .Where(d => d.GroupCode == effectiveGroup)
                .ToListAsync();

            if (!shiftGroupDays.Any())
                return (false, _localizer["BatchShiftNotFound"].Value, 0);

            // 建立每週各天的工時 Map（DayOfWeek 1=週一…7=週日）
            var dayMap = shiftGroupDays.ToDictionary(d => d.DayOfWeek);

            // 預載班制各日所用班別（供可用時段物化：StartTime/EndTime/Break）
            var shiftCodes = shiftGroupDays
                .SelectMany(sgd => new[] { sgd.Shift1Code, sgd.Shift2Code, sgd.Shift3Code, sgd.Shift4Code })
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct().ToList();
            var shiftMap = shiftCodes.Count == 0
                ? new Dictionary<string, Shift>()
                : await _context.Shifts.AsNoTracking()
                    .Where(s => shiftCodes.Contains(s.ShiftCode))
                    .ToDictionaryAsync(s => s.ShiftCode);

            // 決定展開範圍（月份可選，空白=整年）
            var monthStart = month.HasValue ? month.Value : 1;
            var monthEnd   = month.HasValue ? month.Value : 12;

            var rangeStart = new DateOnly(year, monthStart, 1);
            var rangeEnd   = new DateOnly(year, monthEnd, DateTime.DaysInMonth(year, monthEnd));

            // 去重/覆蓋鍵對齊唯一索引 (PlantId, WorkCenterId, MachineCode, Date)，不含 ShiftGroupCode。
            HashSet<DateOnly> existingKeys;
            var existingOvertime = new Dictionary<DateOnly, decimal>();
            if (overwrite)
            {
                // 重產即覆蓋：先刪除範圍內同範圍(廠/WC/機台)既有列，再全部重建（先存檔避免與重建撞唯一索引）
                var toDelete = await _context.Set<WorkCalendar>()
                    .Where(wc => wc.PlantId == plantId
                              && wc.Date >= rangeStart && wc.Date <= rangeEnd
                              && wc.WorkCenterId == wcId
                              && wc.MachineCode == mcId)
                    .ToListAsync();
                if (toDelete.Count > 0)
                {
                    _context.Set<WorkCalendar>().RemoveRange(toDelete);
                    await _context.SaveChangesAsync();
                }
                existingKeys = new HashSet<DateOnly>();
            }
            else
            {
                // 連 TM_WKO 一起撈：可用時段一律重建，而既有表頭列的加班時數可能被使用者
                // 在畫面上改過，重建時要以**現存的那個值**為準，不能拿班制的預設值蓋回去
                // ——否則使用者調過的加班會在下次批次展開時安靜地被還原。
                existingOvertime = (await _context.Set<WorkCalendar>()
                    .Where(wc => wc.PlantId == plantId
                              && wc.Date >= rangeStart && wc.Date <= rangeEnd
                              && wc.WorkCenterId == wcId
                              && wc.MachineCode == mcId)
                    .Select(wc => new { wc.Date, wc.TM_WKO })
                    .ToListAsync()).ToDictionary(x => x.Date, x => x.TM_WKO);
                existingKeys = existingOvertime.Keys.ToHashSet();
            }

            // 載入特殊日（假日 / 補班）
            var specialDays = await _context.Set<SpecialDay>()
                .Where(sd => sd.Date >= rangeStart && sd.Date <= rangeEnd)
                .ToListAsync();
            var specialDayMap = specialDays.ToDictionary(sd => sd.Date);

            // 可用時段（WorkCalendarSegment）一律就範圍/範疇重建（純班別衍生，物化供 Phase C 走步器）。
            var oldSegs = await _context.Set<WorkCalendarSegment>()
                .Where(s => s.PlantId == plantId && s.Date >= rangeStart && s.Date <= rangeEnd
                         && s.WorkCenterId == wcId && s.MachineCode == mcId)
                .ToListAsync();
            if (oldSegs.Count > 0)
            {
                _context.Set<WorkCalendarSegment>().RemoveRange(oldSegs);
                await _context.SaveChangesAsync();
            }

            var toInsert = new List<WorkCalendar>();
            var segInsert = new List<WorkCalendarSegment>();
            var emptyShifts = new List<Shift>();
            var carry = new List<WorkCalendarSegmentBuilder.Segment>();   // 跨午夜尾段，逐日串接

            for (var m = monthStart; m <= monthEnd; m++)
            {
                var daysInMonth = DateTime.DaysInMonth(year, m);
                for (var d = 1; d <= daysInMonth; d++)
                {
                    var date = new DateOnly(year, m, d);
                    // ISO DayOfWeek：1=週一…7=週日
                    var isoDow = ((int)date.DayOfWeek + 6) % 7 + 1;

                    // 決定當日適用排班（供工時與時段共用）
                    ShiftGroupDay? sgd = null;
                    bool isHoliday = false, weekendNoWork = false;
                    string? note = null;
                    if (specialDayMap.TryGetValue(date, out var sd))
                    {
                        if (sd.DayType == "Holiday") { isHoliday = true; note = sd.Description; }
                        else if (sd.DayType == "Overtime")
                        {
                            var targetDow = sd.CompensatoryDayOfWeek ?? isoDow;
                            dayMap.TryGetValue(targetDow, out sgd);
                            note = sd.Description;
                        }
                    }
                    else if (isoDow <= 5) dayMap.TryGetValue(isoDow, out sgd);
                    else weekendNoWork = true;

                    // 物化可用時段（每日皆算以串接跨午夜 carry；假日/週末無班別 → 僅 carry）
                    var dayShifts = (isHoliday || sgd == null) ? emptyShifts : ShiftsOf(sgd, shiftMap);

                    // 加班時數：既有表頭列以現存的 TM_WKO 為準（使用者可能改過），否則用班制當日值。
                    // 不含加班的話排程走步器永遠只看得到正班，而甘特與 OEE 都是含加班算的。
                    var otHours = existingOvertime.TryGetValue(date, out var storedOt)
                        ? storedOt
                        : (!isHoliday && sgd != null ? sgd.DailyOvertimeHours : 0m);
                    var otMinutes = (int)Math.Round(otHours * 60m, MidpointRounding.AwayFromZero);

                    var segs = WorkCalendarSegmentBuilder.BuildDay(
                        dayShifts, carry, out var nextCarry, otMinutes);
                    carry = nextCarry;
                    int seq = 0;
                    foreach (var seg in segs)
                        segInsert.Add(new WorkCalendarSegment
                        {
                            PlantId = plantId, WorkCenterId = wcId, MachineCode = mcId,
                            Date = date, Seq = ++seq, StartMinute = seg.Start, EndMinute = seg.End
                        });

                    // WorkCalendar 表頭列：沿用 skip/週末/假日規則
                    if (existingKeys.Contains(date)) continue;   // 既有列略過（時段已重建於上）
                    if (weekendNoWork) continue;                 // 週末無特殊日 → 不產生表頭列（節省筆數）

                    decimal tmWks = 0, tmWko = 0;
                    TimeOnly? workStart = null, workEnd = null;
                    if (!isHoliday && sgd != null)
                    {
                        tmWks = sgd.DailyRegularHours;
                        tmWko = sgd.DailyOvertimeHours;
                        workStart = sgd.WorkStart;
                        workEnd   = sgd.WorkEnd;
                        if (tmWks >= 24) { workStart = null; workEnd = null; }   // 24h 連續
                    }

                    toInsert.Add(new WorkCalendar
                    {
                        PlantId        = plantId,
                        ShiftGroupCode = effectiveGroup,
                        WorkCenterId   = wcId,
                        MachineCode    = mcId,
                        Date           = date,
                        WorkStart      = workStart,
                        WorkEnd        = workEnd,
                        TM_WKS         = tmWks,
                        TM_WKO         = tmWko,
                        Note           = note
                    });
                }
            }

            if (toInsert.Count > 0) _context.Set<WorkCalendar>().AddRange(toInsert);
            if (segInsert.Count > 0) _context.Set<WorkCalendarSegment>().AddRange(segInsert);
            if (toInsert.Count > 0 || segInsert.Count > 0) await _context.SaveChangesAsync();

            return (true, _localizer["BatchGenerateDone", toInsert.Count].Value, toInsert.Count);
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ShiftGroups = await _context.Set<ShiftGroup>()
                .AsNoTracking()
                .Where(sg => sg.IsActive)
                .OrderBy(sg => sg.GroupCode)
                .Select(sg => new { sg.GroupCode, sg.GroupName })
                .ToListAsync();

            ViewBag.WorkCenters = await _context.Set<SmartFactory.MES.Modules.BAS.Models.WorkCenter>()
                .AsNoTracking()
                .Where(wc => wc.IsActive)
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            await SetPermissionViewBagAsync("WorkCalendar");
        }

        private async Task SetPermissionViewBagAsync(string menuKey)
        {
            ViewBag.CanCreate = await _permissionService.CanCreate(menuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(menuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(menuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(menuKey);
        }
    }
}
