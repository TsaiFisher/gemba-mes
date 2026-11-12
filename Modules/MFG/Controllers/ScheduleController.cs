using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 生產排程（mfg-domain.md Phase C）：執行全量排程、提供甘特圖 JSON 資料。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class ScheduleController : ModuleControllerBase
    {
        private const string MenuKey = "Schedule";

        private readonly ISchedulingEngine _engine;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDateTimeService _dateTime;

        public ScheduleController(
            ISchedulingEngine engine,
            IStringLocalizer<ProjectZSharedResource> localizer,
            IDateTimeService dateTime)
        {
            _engine   = engine;
            _localizer = localizer;
            _dateTime  = dateTime;
        }

        // GET: /Schedule
        public async Task<IActionResult> Index(string? plantId, string? dateTo, string? rule = null)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            plantId ??= UserContext.PlantId;
            // GetLocalDate 的參數是「時區代碼」不是廠別代號：原本傳 plantId（如 "P1"）一律解不開、
            // 退回台北並寫一筆警告（2026-10-19 UI 全套跑出 13 筆），紐約廠的預設排程迄日因此可能差一天。
            var plantTz = await Db.Plants.AsNoTracking()
                .Where(p => p.Id == plantId).Select(p => p.DefaultTimeZone).FirstOrDefaultAsync();
            var toDate = string.IsNullOrEmpty(dateTo)
                ? _dateTime.GetLocalDate(plantTz).AddDays(14)
                : DateOnly.Parse(dateTo);

            ViewBag.PlantId = plantId;
            ViewBag.DateTo  = toDate.ToString("yyyy-MM-dd");
            ViewBag.Rules   = Enum.GetNames<ScheduleRule>();
            // 派工前自動重排：只替使用者目前的廠別觸發（網址帶別廠代碼不得替別廠重排）
            if (plantId == UserContext.PlantId) await HttpContext.EnsureScheduleFreshAsync(plantId);
            var staleness = await LoadStalenessAsync(plantId);
            ViewBag.Staleness = staleness;
            // 規則下拉預設＝本廠自動重排規則，手動按排程才不會不小心用另一套規則把自動重排的結果翻掉
            ViewBag.Rule = Enum.TryParse<ScheduleRule>(rule, out var picked)
                ? picked.ToString()
                : (staleness?.Settings.Rule ?? ScheduleRule.Priority).ToString();
            await PrepareIndexPermissionsAsync(MenuKey);
            return View();
        }

        // POST: /Schedule/Run  — 執行全量排程
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(string? plantId, string rule = "Priority")
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            plantId ??= UserContext.PlantId ?? "";
            if (!Enum.TryParse<ScheduleRule>(rule, out var scheduleRule))
                scheduleRule = ScheduleRule.Priority;

            var today = _dateTime.Now;
            ScheduleRunResult result;
            try
            {
                result = await _engine.RunAsync(plantId, scheduleRule, today, UserContext.UserId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 排程讀取到寫回之間，有派工被報工／開工等作業同時異動（xmin）：整次已回滾，重按即可
                return Json(new { success = false, message = _localizer["ScheduleRunConflict"].Value });
            }
            catch (ScheduleCalendarException ex)
            {
                // 工作日曆排不出時段（例如工作中心沒設班別群組）：整次未寫入，訊息已在地化，直接告訴使用者去補日曆
                return Json(new { success = false, message = ex.Message });
            }
            if (result.Busy)
                return Json(new { success = false, message = _localizer["ScheduleRunBusy"].Value });

            return Json(new
            {
                success = true,
                scheduled = result.Scheduled,
                skipped   = result.Skipped,
                message   = _localizer["ScheduleRunSuccess", result.Scheduled, result.Skipped].Value
            });
        }

        /// <summary>排程頁頂端的過時提示：由資料推導是否過時（見 ScheduleFreshness），加上本廠自動重排設定。</summary>
        private async Task<ScheduleStaleness?> LoadStalenessAsync(string? plantId)
        {
            if (string.IsNullOrEmpty(plantId)) return null;
            var fresh    = await ScheduleFreshness.GetAsync(Db, plantId);
            var settings = await AutoScheduleSettings.LoadAsync(
                HttpContext.RequestServices.GetRequiredService<BAS.Services.IPlantParamService>(), plantId);
            return new ScheduleStaleness(fresh, settings);
        }

        // GET: /Schedule/RunHistory — 排程紀錄：本廠最近幾次全量排程（手動＋自動）與異動筆數
        [HttpGet]
        public async Task<IActionResult> RunHistory(string? plantId, int take = 30)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            plantId ??= UserContext.PlantId;
            if (!CanSeePlant(plantId)) return Forbid();
            take = Math.Clamp(take, 1, 200);

            var rows = await Db.ScheduleRunLogs.AsNoTracking()
                .Where(l => l.PlantId == plantId)
                // 依流水號而非 StartedAt：紀錄在排程跑完時才寫入，流水號就是先後；系統時鐘回撥
                // （WSL 實測一輪六次、每次數秒）會讓後跑的那次 StartedAt 比前一次還早，「最新一筆」就排錯了
                .OrderByDescending(l => l.RunLogId)
                .Take(take)
                .ToListAsync();

            // 執行者顯示名稱（自動重排 RunBy 為 null）
            var userIds = rows.Where(r => r.RunBy != null).Select(r => r.RunBy!).Distinct().ToList();
            var names = await Db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            return Json(new
            {
                rows = rows.Select(r => new
                {
                    id        = r.RunLogId,
                    startedAt = _dateTime.ToLocalTime(r.StartedAt).ToString("yyyy-MM-ddTHH:mm:ss"),
                    trigger   = r.Trigger,
                    runBy     = r.RunBy == null ? null : names.GetValueOrDefault(r.RunBy, r.RunBy),
                    rule      = r.Rule,
                    freezeHours = r.FreezeHours,
                    durationMs = (long)Math.Max(0, (r.FinishedAt - r.StartedAt).TotalMilliseconds),
                    scheduled = r.ScheduledCount,
                    skipped   = r.SkippedCount,
                    fenced    = r.FencedCount,
                    newly     = r.NewlyScheduledCount,
                    moved     = r.MovedCount,
                    machine   = r.MachineChangedCount,
                    unscheduled = r.UnscheduledCount,
                    tomorrow  = r.TomorrowChangedCount,
                    truncated = r.ChangesTruncated,
                })
            });
        }

        // GET: /Schedule/RunChanges/{id} — 單次排程的派工異動明細
        [HttpGet]
        public async Task<IActionResult> RunChanges(long id)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            // ScheduleRunLog 不是廠別實體（背景服務要寫），讀取時自行檢查廠別權限
            var log = await Db.ScheduleRunLogs.AsNoTracking()
                .Where(l => l.RunLogId == id)
                .Select(l => new { l.PlantId, l.ChangesTruncated })
                .FirstOrDefaultAsync();
            if (log == null || !CanSeePlant(log.PlantId)) return NotFound();

            var changes = await Db.ScheduleRunChanges.AsNoTracking()
                .Where(c => c.RunLogId == id)
                .OrderBy(c => c.ChangeId)
                .ToListAsync();

            // 時刻數字即廠別當地時間（UTC 地雷：不可用 "O" 格式，見 GanttData 註解）
            static string? Fmt(DateTime? t) => t?.ToString("yyyy-MM-ddTHH:mm:ss");
            return Json(new
            {
                truncated = log.ChangesTruncated,
                rows = changes.Select(c => new
                {
                    dispatchId  = c.DispatchId,
                    workOrderId = c.WorkOrderId,
                    operationSeq = c.OperationSeq,
                    kind        = c.Kind.ToString(),
                    oldMachine  = c.OldMachineCode,
                    oldStart    = Fmt(c.OldStart),
                    newMachine  = c.NewMachineCode,
                    newStart    = Fmt(c.NewStart),
                })
            });
        }

        /// <summary>排程紀錄不是廠別實體、沒有全域過濾：只給目前廠別或使用者被授權的廠別。</summary>
        private bool CanSeePlant(string? plantId) =>
            !string.IsNullOrEmpty(plantId)
            && (plantId == UserContext.PlantId || UserContext.AllowedPlantIds.Contains(plantId));

        // GET: /Schedule/RunPreview — 執行前提示：會被重新配置的已排派工筆數，以及其中明天的筆數
        [HttpGet]
        public async Task<IActionResult> RunPreview(string? plantId)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            plantId ??= UserContext.PlantId;

            var tomorrow = _dateTime.GetLocalDate().AddDays(1);
            var (tStart, tEnd) = LocalDayUtc(tomorrow);
            var scheduled = Db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == plantId && d.Status == DispatchStatus.Scheduled
                         && Db.WorkOrders.Any(w => w.WorkOrderId == d.WorkOrderId
                                                && (w.Status == WorkOrderStatus.Released
                                                    || w.Status == WorkOrderStatus.InProgress)));
            return Json(new
            {
                scheduled = await scheduled.CountAsync(),
                tomorrow  = await scheduled.CountAsync(d => d.ScheduledStart >= tStart && d.ScheduledStart < tEnd)
            });
        }

        // GET: /Schedule/DispatchPanel/{id}  — 甘特側板（AJAX partial）
        [HttpGet]
        public async Task<IActionResult> DispatchPanel(string id)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            var dispatch = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.DispatchId == id)
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .FirstOrDefaultAsync();

            if (dispatch == null) return NotFound();

            // 可調整條件：Pending 或 Scheduled
            ViewBag.CanAdjust = dispatch.Status == DispatchStatus.Pending
                             || dispatch.Status == DispatchStatus.Scheduled;

            // 機台清單（同工作中心）
            var wcId = dispatch.Operation?.WorkCenterId ?? "";
            ViewBag.Machines = await Db.WorkCenterMachines
                .AsNoTracking()
                .Where(m => m.WorkCenterCode == wcId && m.MachineStatus != BAS.Models.MachineStatus.Retired)
                .Select(m => m.MachineCode)
                .ToListAsync();

            ViewBag.Priority = dispatch.Operation?.WorkOrder?.ProductionBatch?.Priority ?? 100;
            ViewBag.BatchId  = dispatch.Operation?.WorkOrder?.ProductionBatchId ?? "";

            return PartialView("_DispatchPanel", dispatch);
        }

        // GET: /api/mfg/schedule?dateTo=2026-06-30&plantId=P01  — 甘特圖 JSON（計畫+實際雙層）
        //
        // 時間序列化地雷（本檔所有 DateTime 一律用 ToString("yyyy-MM-ddTHH:mm:ss")，
        // 禁用內建 "O" 格式）：本系統房規是 DateTime 數字本身就代表廠別當地時間，
        // Kind=Utc 只是貼給 Npgsql 用的標籤，不是真的做過時區換算。"O" 格式會在字串
        // 尾端加上 "Z"，前端 JS 的 `new Date("...Z")` 會依 ECMAScript 規範把它當成
        // 真正的 UTC 時刻，再用瀏覽器所在時區（台灣 +8）換算成本地時間顯示——等於把
        // 本來就是本地時間的數字又加了一次 +8 小時／跨天，導致甘特圖時段跟實際產能
        // 對不起來。改用不帶 "Z"/時區資訊的格式，前端才會直接當本地時間解析、不再
        // 多換算一次。
        [HttpGet("/api/mfg/schedule")]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> GanttData(string? plantId, string? dateTo)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            plantId ??= UserContext.PlantId;
            var toDate = string.IsNullOrEmpty(dateTo)
                ? _dateTime.GetLocalDate().AddDays(14)
                : DateOnly.Parse(dateTo);
            var fromDate = _dateTime.GetLocalDate();
            var now = _dateTime.Now;

            // timestamptz 欄位：比較值一律 SpecifyKind(Utc)（mfg-domain.md UTC 地雷規則）
            var (fromUtc, _)  = LocalDayUtc(fromDate);
            var (_, toUtc)    = LocalDayUtc(toDate);

            // 取排程後的派工——用「時段與視窗重疊」而非「起點落在視窗內」：
            // 昨天（或更早）開工、現在仍生產中的派工 ScheduledStart < 今天，
            // 若只認起點會整條消失，甘特上看不到正在佔用機台的工作。
            var dispatches = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.ScheduledStart != null && d.ScheduledEnd != null
                         && d.ScheduledEnd >= fromUtc
                         && d.ScheduledStart <= toUtc)
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .ToListAsync();

            // 取進行中派工的開工紀錄（最新的 Open session）
            var dispatchIds = dispatches.Select(d => d.DispatchId).ToList();
            var openSessions = await Db.ProductionSessions
                .AsNoTracking()
                .Where(s => dispatchIds.Contains(s.DispatchId)
                         && s.Status == ProductionSessionStatus.Open)
                .ToListAsync();
            var sessionByDispatch = openSessions
                .GroupBy(s => s.DispatchId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.StartAt).First());

            // 取工作日曆（産能列）：機台/工作中心/全廠三層都撈，交給 BuildGanttJson 依
            // mfg-domain.md §9 三層 fallback 解析（機台>工作中心>全廠）——若只撈機台層
            // （MachineCode != null），全廠共用同一份日曆（未逐機台/工作中心個別維護）
            // 的情況下會查無資料，產能列永遠是空的。
            var calendars = await Db.WorkCalendars
                .AsNoTracking()
                .Where(c => c.PlantId == plantId
                         && c.Date >= fromDate && c.Date <= toDate)
                .ToListAsync();

            // 產能列以「排程引擎實際使用的可用時段」為準（WorkCalendarSegment），不再由
            // WorkStart + TM_WKS 反推——TM_WKS 是**淨**工時（扣掉午休），加回 WorkStart 會把
            // 午休吃掉、收班時間也少算，畫出來的灰帶比真實產能短，看起來像排程排到了沒產能的時段。
            var calSegments = await Db.Set<WorkCalendarSegment>()
                .AsNoTracking()
                .Where(s => s.PlantId == plantId && s.Date >= fromDate && s.Date <= toDate)
                .OrderBy(s => s.Date).ThenBy(s => s.Seq)
                .ToListAsync();

            var conflictTools = DetectToolingConflicts(dispatches);
            var data = BuildGanttJson(dispatches, sessionByDispatch, now, calendars, conflictTools,
                                      fromDate, toDate, calSegments,
                                      _localizer["GanttActualRowSuffix"].Value,
                                      _localizer["GanttOutsourceRow"].Value,
                                      _localizer["GanttCapacityRowSuffix"].Value);

            return Json(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        // GET: /api/mfg/machine-load?plantId=P01&weekOf=2026-06-15  — 機台負荷 JSON
        [HttpGet("/api/mfg/machine-load")]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> MachineLoadData(string? plantId, string? weekOf)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            plantId ??= UserContext.PlantId;
            var baseDate = string.IsNullOrEmpty(weekOf)
                ? _dateTime.GetLocalDate()
                : DateOnly.Parse(weekOf);

            // 取該週一到週日（ISO 週）
            var dayOfWeek = (int)baseDate.DayOfWeek;
            var monday = baseDate.AddDays(-(dayOfWeek == 0 ? 6 : dayOfWeek - 1));
            var sunday = monday.AddDays(6);

            // ScheduledStart/End 是廠別當地時間（Kind=Utc 只是標籤，mfg-domain.md §UTC 地雷），週界線同口徑
            var mondayUtc = DateTime.SpecifyKind(monday.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var nextMondayUtc = mondayUtc.AddDays(7);

            // 已排派工（與本週有交集；已取消的不佔產能）
            var scheduled = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.MachineCode != null
                         && d.ScheduledStart != null
                         && d.ScheduledEnd != null
                         && d.Status != DispatchStatus.Cancelled
                         && d.ScheduledStart < nextMondayUtc
                         && d.ScheduledEnd > mondayUtc)
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .ToListAsync();

            // 未排派工數（Pending 且無 ScheduledStart）
            var unscheduledByMachine = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.MachineCode != null
                         && d.ScheduledStart == null
                         && d.Status == DispatchStatus.Pending)
                .GroupBy(d => d.MachineCode!)
                .Select(g => new { Machine = g.Key, Count = g.Count() })
                .ToListAsync();
            var unscheduledMap = unscheduledByMachine.ToDictionary(x => x.Machine, x => x.Count);

            // 機台 → 工作中心對應（供日曆三層 fallback 用；不能只靠有派工的機台反推）
            var machineToWc = await Db.WorkCenterMachines
                .AsNoTracking()
                .ToDictionaryAsync(m => m.MachineCode, m => m.WorkCenterCode);

            // 已排與可用用**同一份**日曆時段（排程引擎實際排進去的那份，機台 → 工作中心 → 全廠 fallback）。
            // 原本已排取「結束 − 開始」的牆鐘時間（含夜間、假日），可用取日曆工時，
            // 一張跨夜或跨週的派工就能把利用率灌到 300% 以上（2026-10-27 回報 323%）；
            // 且只把單張上限截在一週長度，沒有截掉落在本週以外的部分。
            // 計劃停機（排程引擎排不進去的時段）在這裡先從當日時段挖掉，已排與可用一起扣
            var ctx = await _engine.LoadCalendarContextAsync(plantId!, monday, sunday);
            IReadOnlyList<(int Start, int End)> Segs(string machine, DateOnly date)
            {
                var wc = machineToWc.TryGetValue(machine, out var w) ? w : null;
                var dayStart = date.ToDateTime(TimeOnly.MinValue);
                var downs = ctx.GetDowntimeRanges(machine, wc)
                    .Select(r => (From: (int)Math.Floor((r.From - dayStart).TotalMinutes),
                                  To:   (int)Math.Ceiling((r.To - dayStart).TotalMinutes)))
                    .Where(r => r.To > 0 && r.From < 1440)
                    .ToList();
                var result = new List<(int Start, int End)>();
                foreach (var s in ctx.GetSegments(machine, wc, date))
                {
                    var pieces = new List<(int Start, int End)> { (s.Start, s.End) };
                    foreach (var (f, t) in downs)
                        pieces = pieces.SelectMany(p => t <= p.Start || f >= p.End
                                ? new[] { p }
                                : new[] { (Start: p.Start, End: Math.Max(p.Start, f)), (Start: Math.Min(p.End, t), End: p.End) })
                            .Where(p => p.End > p.Start).ToList();
                    result.AddRange(pieces);
                }
                return result;
            }

            double AvailableMinutes(string machine)
            {
                double total = 0;
                for (var date = monday; date <= sunday; date = date.AddDays(1))
                    total += Segs(machine, date).Sum(s => s.End - s.Start);
                return total;
            }

            double ScheduledWorkingMinutes(string machine, DateTime start, DateTime end)
            {
                var from = start < mondayUtc ? mondayUtc : start;
                var to   = end > nextMondayUtc ? nextMondayUtc : end;
                return (double)QueueTimeSuggestionService.WorkingMinutesBetween(
                    DateTime.SpecifyKind(from, DateTimeKind.Unspecified),
                    DateTime.SpecifyKind(to, DateTimeKind.Unspecified),
                    date => Segs(machine, date));
            }

            // 彙整
            var allMachines = scheduled.Select(d => d.MachineCode!).Distinct()
                .Union(unscheduledMap.Keys)
                .OrderBy(m => m)
                .ToList();

            var rows = allMachines.Select(machine =>
            {
                var macDispatches = scheduled.Where(d => d.MachineCode == machine).ToList();
                var scheduledMin = macDispatches.Sum(d =>
                    ScheduledWorkingMinutes(machine, d.ScheduledStart!.Value, d.ScheduledEnd!.Value));
                var availMin = AvailableMinutes(machine);
                var utilPct  = availMin > 0 ? Math.Round(scheduledMin / availMin * 100, 1) : (scheduledMin > 0 ? 999.9 : 0.0);
                var lateCount = macDispatches.Count(d => d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true);
                var wcId = macDispatches.FirstOrDefault()?.Operation?.WorkCenterId
                           ?? (machineToWc.TryGetValue(machine, out var wc0) ? wc0 : "");

                return (object)new
                {
                    machine,
                    workCenter = wcId,
                    scheduledMin  = (int)scheduledMin,
                    availableMin  = (int)availMin,
                    utilizationPct = utilPct,
                    overloaded = utilPct > 100,
                    unscheduledCount = unscheduledMap.TryGetValue(machine, out var uc) ? uc : 0,
                    lateCount
                };
            }).ToList();

            return Json(rows, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        // GET: /api/mfg/preparation?plantId=P01&date=2026-06-17  — T-1 生產準備看板 JSON（§23.3）
        [HttpGet("/api/mfg/preparation")]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> PreparationData(string? plantId, string? date)
        {
            // 三個呼叫端：PreparationBoard/Index、Schedule/Index，以及**手機** mfg/preparation.page.js。
            // 手機頁本身由行動權限 ScheduleView 控管，但它打的是這支桌面 API，
            // 只鎖 PreparationBoard 會讓「只給手機作業」的角色看板整片空白。
            if (!await Permissions.CanReadAny("PreparationBoard", MenuKey)) return Forbid();

            plantId ??= UserContext.PlantId;
            // 準備看板（桌機、手機共用此 API）：派工前自動重排，看到的明日計畫是最新的
            if (plantId == UserContext.PlantId) await HttpContext.EnsureScheduleFreshAsync(plantId);
            var targetDate = string.IsNullOrEmpty(date)
                ? _dateTime.GetLocalDate().AddDays(1)   // 預設明日
                : DateOnly.Parse(date);

            var (dayStart, dayEnd) = LocalDayUtc(targetDate);

            // 取目標日「有在做」的派工：當天開始的，加上**前一天（或更早）開始、跨到當天**的。
            // 原本只取「起點落在當天」——跨日的長單、昨天開工還在生產的派工整條不見，
            // 甘特（用的是時段重疊）上看得到、看板卻顯示「該日無已排程派工」（2026-10-05 回報）。
            // 跨入的那幾張標 carryOver，前端標「延續」：它們不需要再備料／換模，但機台當天是被佔用的。
            var dispatches = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.ScheduledStart != null
                         && d.ScheduledStart < dayEnd
                         && (d.ScheduledStart >= dayStart
                             || (d.ScheduledEnd != null && d.ScheduledEnd > dayStart
                                 && d.Status != DispatchStatus.Closed)))
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .Include(d => d.Materials)
                .OrderBy(d => d.MachineCode)
                .ThenBy(d => d.ScheduledStart)
                .ToListAsync();

            // 計算模具衝突：同一 ToolingCode 排於重疊時段的不同機台
            var conflictTools = DetectToolingConflicts(dispatches);

            // 取關聯備料單（MaterialStagingOrder）
            var woIds = dispatches.Select(d => d.WorkOrderId).Distinct().ToList();
            var stagingByWo = await Db.Set<SmartFactory.MES.Modules.INV.Models.MaterialStagingOrder>()
                .AsNoTracking()
                .Where(s => s.SourceId != null && woIds.Contains(s.SourceId)
                         && s.PlantId == plantId)
                .Select(s => new { s.SourceId, s.Status })
                .ToListAsync();
            var stagingMap = stagingByWo
                .GroupBy(s => s.SourceId!)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Status.ToString()).ToList());

            var rows = dispatches.Select(d => (object)new
            {
                dispatchId     = d.DispatchId,
                workOrderId    = d.WorkOrderId,
                machineCode    = d.MachineCode,
                operationSeq   = d.OperationSeq,
                operationName  = d.Operation?.OperationName,
                workCenterId   = d.Operation?.WorkCenterId,
                qty            = d.Quantity,
                scheduledStart = d.ScheduledStart?.ToString("yyyy-MM-ddTHH:mm:ss"),
                scheduledEnd   = d.ScheduledEnd?.ToString("yyyy-MM-ddTHH:mm:ss"),
                setupMin = d.Operation != null
                    ? (int)OperationTimeUnitExtensions.ToMinutes(d.Operation.SetupTime, d.Operation.SetupTimeUnit)
                    : 0,
                toolingCode    = d.ToolingCode,
                toolingConflict = d.ToolingCode != null && conflictTools.Contains(d.ToolingCode),
                materialRisk   = d.MaterialRisk,
                late           = d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true,
                stagingStatuses = stagingMap.TryGetValue(d.WorkOrderId, out var ss) ? ss : new List<string>(),
                materialCount  = d.Materials.Count,
                carryOver      = d.ScheduledStart < dayStart,
            }).ToList();

            return Json(new { date = targetDate.ToString("yyyy-MM-dd"), rows },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
        }

        /// <summary>
        /// 本地日 → UTC 區間 [起, 迄)。ScheduledStart/End 是 timestamptz（UTC）。
        /// 原本把本地日期直接標成 UTC 午夜：台灣的「10/5」被當成 10/5 08:00～10/6 07:59，
        /// 凌晨 0～8 點開工的派工落到前一天、隔天凌晨的又被算進來；迄時用 23:59 還漏掉最後一分鐘。
        /// </summary>
        private (DateTime StartUtc, DateTime EndUtc) LocalDayUtc(DateOnly date)
        {
            var start = _dateTime.ToUtcTime(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified));
            var end   = _dateTime.ToUtcTime(DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified));
            return (DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc));
        }

        // GET: /api/mfg/work-centers  — 工作中心清單（手機查詢下拉）
        [HttpGet("/api/mfg/work-centers")]
        public async Task<IActionResult> WorkCenterList()
        {
            // 排程查詢頁（含手機 schedule-query）的篩選下拉；不是 BAS 工作中心維護畫面的資料來源。
            if (!await Permissions.CanReadAny("ScheduleQuery", MenuKey)) return Forbid();

            var list = await Db.WorkCenters
                .AsNoTracking()
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            return Json(list, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        // GET: /api/mfg/tooling-conflicts?plantId=P01&dateFrom=2026-06-17&dateTo=2026-06-20
        [HttpGet("/api/mfg/tooling-conflicts")]
        public async Task<IActionResult> ToolingConflicts(string? plantId, string? dateFrom, string? dateTo)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            plantId ??= UserContext.PlantId;
            var from = string.IsNullOrEmpty(dateFrom)
                ? _dateTime.GetLocalDate()
                : DateOnly.Parse(dateFrom);
            var to = string.IsNullOrEmpty(dateTo)
                ? from.AddDays(14)
                : DateOnly.Parse(dateTo);

            var fromUtc = DateTime.SpecifyKind(from.ToDateTime(new TimeOnly(0, 0)), DateTimeKind.Utc);
            var toUtc   = DateTime.SpecifyKind(to.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Utc);

            var dispatches = await Db.Dispatches
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.ToolingCode != null
                         && d.ScheduledStart != null && d.ScheduledEnd != null
                         && d.ScheduledStart >= fromUtc && d.ScheduledStart <= toUtc)
                .Select(d => new
                {
                    d.DispatchId, d.WorkOrderId, d.MachineCode,
                    d.ToolingCode, d.ScheduledStart, d.ScheduledEnd
                })
                .ToListAsync();

            // 找重疊衝突：同一 ToolingCode，不同機台，時段重疊
            var conflicts = new List<object>();
            var byTool = dispatches.GroupBy(d => d.ToolingCode!).ToList();
            foreach (var grp in byTool)
            {
                var items = grp.OrderBy(d => d.ScheduledStart).ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        var a = items[i]; var b = items[j];
                        if (a.MachineCode == b.MachineCode) continue;
                        // 時段重疊判定
                        if (a.ScheduledStart < b.ScheduledEnd && b.ScheduledStart < a.ScheduledEnd)
                        {
                            conflicts.Add(new
                            {
                                toolingCode = grp.Key,
                                dispatch1 = a.DispatchId, machine1 = a.MachineCode,
                                start1 = a.ScheduledStart?.ToString("yyyy-MM-ddTHH:mm:ss"), end1 = a.ScheduledEnd?.ToString("yyyy-MM-ddTHH:mm:ss"),
                                dispatch2 = b.DispatchId, machine2 = b.MachineCode,
                                start2 = b.ScheduledStart?.ToString("yyyy-MM-ddTHH:mm:ss"), end2 = b.ScheduledEnd?.ToString("yyyy-MM-ddTHH:mm:ss")
                            });
                        }
                    }
                }
            }

            return Json(conflicts, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>計算給定派工清單中的模具衝突 ToolingCode 集合。</summary>
        private static HashSet<string> DetectToolingConflicts(IReadOnlyList<Dispatch> dispatches)
        {
            var conflicted = new HashSet<string>();
            var withTool = dispatches
                .Where(d => d.ToolingCode != null && d.ScheduledStart.HasValue && d.ScheduledEnd.HasValue)
                .ToList();

            var byTool = withTool.GroupBy(d => d.ToolingCode!).ToList();
            foreach (var grp in byTool)
            {
                var items = grp.OrderBy(d => d.ScheduledStart).ToList();
                for (int i = 0; i < items.Count; i++)
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        var a = items[i]; var b = items[j];
                        if (a.MachineCode == b.MachineCode) continue;
                        if (a.ScheduledStart < b.ScheduledEnd && b.ScheduledStart < a.ScheduledEnd)
                            conflicted.Add(grp.Key);
                    }
            }
            return conflicted;
        }

        // ── Gantt JSON builder ─────────────────────────────────────────────────

        /// <summary>
        /// 派工 bar 的著色分類。優先序：進行中 &gt; 延遲預測 &gt; 模具衝突 &gt; 待料風險 &gt; 一般已排程。
        ///
        /// 回傳「穩定代碼 + 顯示字串」兩者刻意分開：**代碼是前端色表的鍵，顯示字串只給圖例與
        /// tooltip 看**。這個畫面的列標籤（`{機台}産能`、`委外`）目前都是硬編中文，日後若一併
        /// 在地化，改的是顯示字串，顏色不會跟著壞。
        ///
        /// ⚠️ TimelinesChart 畫 bar 只吃 `zColorScale(val)`，不支援逐 segment 自訂色
        /// （2.14.3 升級時再次查證未變）。因此**顏色只有一個通道**：val 拿去表狀態，就不能同時
        /// 表批號——批號改由前端「著色依據」下拉切換。原本後端塞的 `col` 欄位從未套用到畫面上，
        /// 已一併移除，避免再有人以為改它會生效。
        /// </summary>
        private static (string Cat, string Label) ResolveDispatchCategory(
            bool inProgress, bool isLate, bool toolConflict, bool materialRisk)
            => inProgress    ? ("inProgress",   PzText.Get("Mfg_GanttCat_InProgress"))
             : isLate        ? ("late",         PzText.Get("Mfg_GanttCat_Late"))
             : toolConflict  ? ("toolConflict", PzText.Get("Mfg_GanttCat_ToolConflict"))
             : materialRisk  ? ("matRisk",      PzText.Get("Mfg_GanttCat_MatRisk"))
             :                 ("planned",      PzText.Get("Mfg_GanttCat_Planned"));

        /// <summary>委外列的內部分組鍵（顯示一律換成 outsourceLabel）。不用中文，避免與顯示字混用。</summary>
        private const string OutsourceGroupKey = "__outsource__";

        private static List<object> BuildGanttJson(
            IReadOnlyList<Dispatch> dispatches,
            IReadOnlyDictionary<string, ProductionSession> sessionByDispatch,
            DateTime now,
            IReadOnlyList<WorkCalendar> calendars,
            HashSet<string>? conflictTools,
            DateOnly fromDate,
            DateOnly toDate,
            IReadOnlyList<WorkCalendarSegment>? calSegments,
            string actualRowSuffix,
            string outsourceLabel,
            string capacityRowSuffix)
        {
            // 可用時段三層 fallback（機台 > 工作中心 > 全廠），與 CalendarContext.GetSegments 同規則，
            // 確保產能列畫出來的就是排程引擎真正拿來排的時段。
            var segList = calSegments ?? Array.Empty<WorkCalendarSegment>();
            var segByMachine = segList
                .Where(s => s.MachineCode != null)
                .GroupBy(s => s.MachineCode!)
                .ToDictionary(g => g.Key, g => g.GroupBy(s => s.Date)
                                                .ToDictionary(x => x.Key, x => x.ToList()));
            var segByWorkCenter = segList
                .Where(s => s.MachineCode == null && s.WorkCenterId != null)
                .GroupBy(s => s.WorkCenterId!)
                .ToDictionary(g => g.Key, g => g.GroupBy(s => s.Date)
                                                .ToDictionary(x => x.Key, x => x.ToList()));
            var segPlant = segList
                .Where(s => s.MachineCode == null && s.WorkCenterId == null)
                .GroupBy(s => s.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            List<WorkCalendarSegment> ResolveSegments(string machine, string wcId, DateOnly date) =>
                (segByMachine.TryGetValue(machine, out var bm) && bm.TryGetValue(date, out var m)) ? m
                : (segByWorkCenter.TryGetValue(wcId, out var bw) && bw.TryGetValue(date, out var w)) ? w
                : segPlant.TryGetValue(date, out var p) ? p : new List<WorkCalendarSegment>();

            // group by WorkCenter
            var wcDispatch = dispatches
                .Where(d => d.Operation?.WorkCenterId != null)
                .GroupBy(d => d.Operation!.WorkCenterId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 產能列三層 fallback（機台 > 工作中心 > 全廠，mfg-domain.md §9）：
            // 全廠共用同一份日曆時只會有全廠層資料（機台/工作中心欄位皆為 null），
            // 逐機台畫產能列時仍須能解析到這層，不能只認機台層。
            var calByMachine = calendars
                .Where(c => c.MachineCode != null)
                .GroupBy(c => c.MachineCode!)
                .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Date));
            var calByWorkCenter = calendars
                .Where(c => c.MachineCode == null && c.WorkCenterId != null)
                .GroupBy(c => c.WorkCenterId!)
                .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Date));
            var calPlant = calendars
                .Where(c => c.MachineCode == null && c.WorkCenterId == null)
                .ToDictionary(c => c.Date);

            WorkCalendar? ResolveCalendar(string machine, string wcId, DateOnly date) =>
                (calByMachine.TryGetValue(machine, out var byMachine) && byMachine.TryGetValue(date, out var m)) ? m
                : (calByWorkCenter.TryGetValue(wcId, out var byWc) && byWc.TryGetValue(date, out var w)) ? w
                : calPlant.TryGetValue(date, out var p) ? p : null;

            // Collect all WCs (from dispatches + outsource)
            var allWcs = dispatches
                .Select(d => d.Operation?.WorkCenterId ?? OutsourceGroupKey)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (!allWcs.Contains(OutsourceGroupKey) && dispatches.Any(d => d.IsOutsource))
                allWcs.Add(OutsourceGroupKey);

            var result = new List<object>();
            foreach (var wc in allWcs)
            {
                var wcData = new List<object>();

                var dispatchRows = wcDispatch.TryGetValue(wc, out var dList) ? dList : new List<Dispatch>();
                // 只顯示這個工作中心底下「查詢時段內真的有派工」的機台——工作中心掛的其他
                // 機台即使存在，只要這段時間沒有排程就不畫，避免整排空的產能列洗版。
                var allMachines = dispatchRows
                    .Where(d => d.MachineCode != null)
                    .Select(d => d.MachineCode!)
                    .Distinct()
                    .OrderBy(m => m)
                    .ToList();

                foreach (var machine in allMachines)
                {
                    // 生產中的先畫（陣列最前），其餘依排程起點——正在佔用機台的
                    // 工作優先呈現，即使歷史資料有時段重疊也不會被後畫的計畫條蓋住判讀。
                    var machineDispatches = dispatchRows
                        .Where(d => d.MachineCode == machine)
                        .OrderByDescending(d => d.Status == DispatchStatus.InProgress)
                        .ThenBy(d => d.ScheduledStart)
                        .ToList();

                    // 計畫派工條（標色依狀態/延誤/待料）
                    var dispatchEntries = new List<object>();
                    var actualEntries   = new List<object>();

                    foreach (var d in machineDispatches)
                    {
                        var isLate     = d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true;
                        var inProgress = d.Status == DispatchStatus.InProgress;

                        var toolConflict = d.ToolingCode != null
                                        && (conflictTools?.Contains(d.ToolingCode) == true);

                        var (cat, catLabel) = ResolveDispatchCategory(
                            inProgress, isLate, toolConflict, d.MaterialRisk);

                        dispatchEntries.Add(new
                        {
                            timeRange = new[]
                            {
                                d.ScheduledStart!.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                                d.ScheduledEnd!.Value.ToString("yyyy-MM-ddTHH:mm:ss")
                            },
                            // val ＝ 畫面著色/圖例的鍵。預設走狀態分類（少數固定色，例外才跳色）；
                            // 前端「著色依據」切到批號時會改用下面的 batch 重算 val。
                            val   = catLabel,
                            cat,
                            batch = d.Operation?.WorkOrder?.ProductionBatchId ?? d.WorkOrderId,
                            ww    = d.DispatchId,
                            wo    = d.WorkOrderId,
                            dtDlv = d.Operation?.WorkOrder?.PlannedEndDate.ToString("yyyy-MM-dd"),
                            wh    = (int)SchedulingEngine.CalcRequiredMinutes(d.Operation!, d.Quantity),
                            pir   = d.Operation?.WorkOrder?.ProductionBatch?.Priority ?? 0,
                            done  = (int)d.QtyDone,
                            qty   = (int)d.Quantity,
                            late  = isLate,
                            matRisk   = d.MaterialRisk,
                            toolConflict,
                            toolingCode = d.ToolingCode
                        });

                        // 實際進度條（進行中且有 Open session）——畫到「現在」：
                        // 生產超時（now > ScheduledEnd）時不得截到計畫終點，
                        // 超出計畫條的部分正是「還在生產且已超時」的關鍵訊號。
                        if (inProgress && sessionByDispatch.TryGetValue(d.DispatchId, out var sess))
                        {
                            var actualEnd = now;
                            actualEntries.Add(new
                            {
                                timeRange = new[]
                                {
                                    sess.StartAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                                    actualEnd.ToString("yyyy-MM-ddTHH:mm:ss")
                                },
                                val = "▶" + actualRowSuffix,
                                cat = "actual",
                                ww  = d.DispatchId,
                                wo  = d.WorkOrderId,
                                done = (int)d.QtyDone,
                                qty  = (int)d.Quantity
                            });
                        }
                    }

                    wcData.Add(new { label = machine, data = dispatchEntries });

                    if (actualEntries.Count > 0)
                        // 實際進度列的 y 軸列名。這裡是**列名**不是 segment 的 val，
                        // 走不到 Index.cshtml 的 GANTT_CAT_LABELS 替換，故必須在後端就在地化，
                        // 否則英文介面會出現「WCA13實際」這種中英混雜的列名。
                        wcData.Add(new
                        {
                            label = $"{machine}{actualRowSuffix}",
                            data  = actualEntries
                        });

                    // 産能列：逐日以三層 fallback 解析（機台 > 工作中心 > 全廠）。
                    // 正班一律畫 WorkCalendarSegment 的**每一段**（午休/班距自然形成空白），
                    // 這是排程引擎唯一認得的產能來源，畫面與排程結果才不會各說各話。
                    // 產能列固定用灰階（不落入派工列的狀態色階，避免跟派工色混淆）。
                    // ⚠️ 上色一律在 Index.cshtml 的 ganttColorScale，依這裡送的 cat 代碼查表。
                    var capacityEntries = new List<object>();
                    for (var date = fromDate; date <= toDate; date = date.AddDays(1))
                    {
                        var cal  = ResolveCalendar(machine, wc, date);
                        var segs = ResolveSegments(machine, wc, date);
                        var dayZero = date.ToDateTime(new TimeOnly(0, 0));

                        DateTime? regEnd = null;
                        foreach (var s in segs.OrderBy(s => s.StartMinute))
                        {
                            if (s.EndMinute <= s.StartMinute) continue;
                            var segStart = dayZero.AddMinutes(s.StartMinute);
                            var segEnd   = dayZero.AddMinutes(s.EndMinute);
                            capacityEntries.Add(new
                            {
                                timeRange = new[] { segStart.ToString("yyyy-MM-ddTHH:mm:ss"), segEnd.ToString("yyyy-MM-ddTHH:mm:ss") },
                                val = PzText.Get("Mfg_GanttRegularShift"),
                                cat = "shiftRegular"
                            });
                            regEnd = segEnd;
                        }

                        // 尚未物化時段的日曆（舊資料／未按「批次展開」）：退回 WorkStart+TM_WKS 概略畫，
                        // 至少不讓產能列整片消失；此時灰帶不含午休，與排程可能略有落差。
                        if (regEnd == null && cal != null && cal.TM_WKS > 0)
                        {
                            var dayStart = cal.Date.ToDateTime(cal.WorkStart ?? new TimeOnly(0, 0));
                            regEnd = dayStart.AddHours((double)cal.TM_WKS);
                            capacityEntries.Add(new
                            {
                                timeRange = new[] { dayStart.ToString("yyyy-MM-ddTHH:mm:ss"), regEnd.Value.ToString("yyyy-MM-ddTHH:mm:ss") },
                                val = PzText.Get("Mfg_GanttRegularShift"),
                                cat = "shiftRegular"
                            });
                        }

                        // 加班接在正班最後一段之後。
                        // ⚠️ **這段的畫法與 WorkCalendarSegmentBuilder.BuildDay 的加班錨點必須一致**
                        // （同樣是「接在當日班別最後一段之後」），否則畫出來的加班與排得進去的加班會錯開。
                        // 2026-08-26 之前這裡註明「僅作為視覺參考」，因為加班不進 WorkCalendarSegment、
                        // 排程引擎看不到它；那個落差已修（見 mfg-domain.md §產能模型），註解一併更正——
                        // 留著舊註解比沒有註解更糟，它會讓下一個人以為排程仍然不吃加班。
                        if (regEnd != null && cal != null && cal.TM_WKO > 0)
                        {
                            var otEnd = regEnd.Value.AddHours((double)cal.TM_WKO);
                            capacityEntries.Add(new
                            {
                                timeRange = new[] { regEnd.Value.ToString("yyyy-MM-ddTHH:mm:ss"), otEnd.ToString("yyyy-MM-ddTHH:mm:ss") },
                                val = PzText.Get("Mfg_GanttOvertime"),
                                cat = "shiftOvertime"
                            });
                        }
                    }
                    wcData.Add(new { label = $"{machine}{capacityRowSuffix}", data = capacityEntries });
                }

                // 委外行
                var outsourceDispatches = dispatchRows.Where(d => d.IsOutsource).ToList();
                if (outsourceDispatches.Any())
                {
                    // 委外列與機台列走同一套狀態分類，否則同一張派工在兩條列上會是兩種顏色。
                    var entries = outsourceDispatches.Select(d =>
                    {
                        var isLate     = d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true;
                        var inProgress = d.Status == DispatchStatus.InProgress;
                        var toolConflict = d.ToolingCode != null
                                        && (conflictTools?.Contains(d.ToolingCode) == true);
                        var (cat, catLabel) = ResolveDispatchCategory(
                            inProgress, isLate, toolConflict, d.MaterialRisk);

                        return (object)new
                        {
                            timeRange = new[]
                            {
                                d.ScheduledStart!.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                                d.ScheduledEnd!.Value.ToString("yyyy-MM-ddTHH:mm:ss")
                            },
                            val   = catLabel,
                            cat,
                            batch = d.Operation?.WorkOrder?.ProductionBatchId ?? d.WorkOrderId,
                            ww    = d.DispatchId,
                            wo    = d.WorkOrderId,
                            late  = isLate,
                            matRisk = d.MaterialRisk,
                            toolConflict
                        };
                    }).ToList();
                    wcData.Add(new { label = outsourceLabel, data = entries });
                }

                if (wcData.Count > 0)
                    result.Add(new { group = wc == OutsourceGroupKey ? outsourceLabel : wc, data = wcData });
            }

            return result;
        }
    }

    /// <summary>排程頁頂端的過時提示資料。</summary>
    public record ScheduleStaleness(ScheduleFreshnessInfo Fresh, AutoScheduleSettings Settings);
}
