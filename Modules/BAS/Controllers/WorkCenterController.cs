using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class WorkCenterController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "WorkCenterMaster";

        public WorkCenterController(ApplicationDbContext context, IApprovalService approvalService,
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

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }


        private async Task PrepareFormDropdowns()
        {
            var allUnits = await _context.OrganizationUnits
                .IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var currentPlantId = _userContext.PlantId;

            ViewBag.DeptOptions = allUnits
                .Where(u => GetEffectivePlantId(u, allUnits) == currentPlantId)
                .OrderBy(u => u.Name)
                .Select(u => new { u.Id, u.Name })
                .ToList();

            ViewBag.ShiftGroupOptions = await _context.ShiftGroups
                .AsNoTracking()
                .Where(s => s.IsActive && s.PlantId == _userContext.PlantId)
                .Select(s => new { s.GroupCode, s.GroupName })
                .ToListAsync();

            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            // 預設備料倉：備料倉型別的倉庫排在前面（多數情況只會選這些），但不排除其他倉別——
            // 沒有另闢備料倉、只在原料倉劃一塊備料區的廠仍要能設定。
            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking()
                .Where(w => w.IsActive)
                .OrderByDescending(w => w.WarehouseType == WarehouseType.Staging)
                .ThenBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName, IsStaging = w.WarehouseType == WarehouseType.Staging })
                .ToListAsync();

            var locations = await _context.StorageLocations
                .ForCurrentPlant(_context)
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName })
                .ToListAsync();

            ViewBag.LocationOptions = locations;

            // 供面板前端做倉庫→儲位過濾用。在此序列化（而非 View 內）以確保 camelCase 與
            // 前端 bindStagingWarehouse() 讀取的欄位名一致。
            ViewBag.LocationsJson = System.Text.Json.JsonSerializer.Serialize(locations,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
        }

        // GET: /WorkCenter
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

            var allUnitsForIndex = await _context.OrganizationUnits
                .IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var currentPlantId = _userContext.PlantId;
            ViewBag.DeptNames = allUnitsForIndex
                .Where(u => GetEffectivePlantId(u, allUnitsForIndex) == currentPlantId)
                .ToDictionary(u => u.Id, u => u.Name);
            ViewBag.ShiftGroupNames = await _context.ShiftGroups.AsNoTracking()
                .Where(s => s.IsActive && s.PlantId == _userContext.PlantId)
                .ToDictionaryAsync(s => s.GroupCode, s => s.GroupName);

            if (!searched) return View((PaginatedList<WorkCenter>?)null);

            var query = _context.WorkCenters.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.WorkCenterCode.ToLower().Contains(lower) ||
                    t.WorkCenterName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.WorkCenterCode),
                "name_asc"  => query.OrderBy(t => t.WorkCenterName),
                "name_desc" => query.OrderByDescending(t => t.WorkCenterName),
                _           => query.OrderBy(t => t.WorkCenterCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<WorkCenter>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /WorkCenter/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            var model = new WorkCenter { IsActive = true };
            if (IsAjax) return PartialView("_WorkCenterForm", model);
            return View("_WorkCenterForm", model);
        }


        /// <summary>
        /// 班別群組與備料倉／儲位必須存在、屬於本廠、且未停用。
        ///
        /// 掛到別廠的班別群組不會報錯，只會讓排程用另一個廠的行事曆去算這座工作中心的產能；
        /// 備料倉指到別廠則是備料單一路開到查不到的倉去。兩者都是「存得進去、之後才錯」。
        /// </summary>
        private async Task ValidateReferencesAsync(WorkCenter model)
        {
            var plantId = _userContext.PlantId;

            var sg = await MasterReferenceGuard.CheckShiftGroupAsync(_context, model.ShiftGroupCode, plantId);
            AddRefError(nameof(model.ShiftGroupCode), sg, model.ShiftGroupCode);

            var wh = await MasterReferenceGuard.CheckWarehouseAsync(_context, model.StagingWhCode, plantId);
            AddRefError(nameof(model.StagingWhCode), wh, model.StagingWhCode);

            var loc = await MasterReferenceGuard.CheckLocationAsync(
                _context, model.StagingWhCode, model.StagingLocationCode, plantId);
            AddRefError(nameof(model.StagingLocationCode), loc, model.StagingLocationCode);
        }

        private void AddRefError(string field, MasterRefStatus status, string? code)
        {
            var key = MasterReferenceGuard.MessageKey(status);
            if (key == null) return;
            ModelState.AddModelError(field, string.Format(_localizer[key].Value, code));
        }

        // POST: /WorkCenter/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(WorkCenter.WorkCenterCode), nameof(WorkCenter.WorkCenterName), nameof(WorkCenter.DepartmentId),
            nameof(WorkCenter.ShiftGroupCode), nameof(WorkCenter.IsProductionLine), nameof(WorkCenter.AdditionalRate),
            nameof(WorkCenter.StandardLaborCost), nameof(WorkCenter.MachineHourlyRate),
            nameof(WorkCenter.StagingWhCode), nameof(WorkCenter.StagingLocationCode),
            nameof(WorkCenter.IsActive), nameof(WorkCenter.Remarks))] WorkCenter model, WorkCenterMachine[]? machines)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            // Machine WorkCenterCode is assigned by controller, not form — remove from validation
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("machines", StringComparison.OrdinalIgnoreCase)).ToList())
                ModelState.Remove(key);
            await ValidateReferencesAsync(model);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_WorkCenterForm", model);
                return View("_WorkCenterForm", model);
            }

            var exists = await _context.WorkCenters.AnyAsync(t => t.WorkCenterCode == model.WorkCenterCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.WorkCenterCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_WorkCenterForm", model);
                return View("_WorkCenterForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            // 生產線（IsProductionLine）也可掛列：每筆語義上是一條「線別」，排程視為平行資源
            if (machines != null)
            {
                model.Machines = machines
                    .Where(m => !string.IsNullOrWhiteSpace(m.MachineCode) && !string.IsNullOrWhiteSpace(m.MachineName))
                    .Select(m => new WorkCenterMachine
                    {
                        WorkCenterCode = model.WorkCenterCode,
                        MachineCode    = m.MachineCode.Trim(),
                        MachineName    = m.MachineName.Trim(),
                        MachineStatus  = m.MachineStatus,
                        Remarks        = m.Remarks
                    }).ToList();
            }

            _context.WorkCenters.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /WorkCenter/Edit/{workCenterCode}
        [HttpGet]
        public async Task<IActionResult> Edit(string workCenterCode)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.WorkCenters
                .Include(wc => wc.Machines.OrderBy(m => m.MachineCode))
                .FirstOrDefaultAsync(wc => wc.WorkCenterCode == workCenterCode);
            if (rec == null) return NotFound();

            await PrepareFormDropdowns();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_WorkCenterForm", rec);
            return View("_WorkCenterForm", rec);
        }

        // POST: /WorkCenter/Edit/{workCenterCode}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string workCenterCode, WorkCenter updated, WorkCenterMachine[]? machines)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            // Machine WorkCenterCode is assigned by controller, not form — remove from validation
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("machines", StringComparison.OrdinalIgnoreCase)).ToList())
                ModelState.Remove(key);
            await ValidateReferencesAsync(updated);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_WorkCenterForm", updated);
                return View("_WorkCenterForm", updated);
            }

            var rec = await _context.WorkCenters
                .FirstOrDefaultAsync(wc => wc.WorkCenterCode == workCenterCode);
            if (rec == null) return NotFound();

            rec.WorkCenterName    = updated.WorkCenterName;
            rec.DepartmentId      = updated.DepartmentId;
            rec.ShiftGroupCode    = updated.ShiftGroupCode;
            rec.IsProductionLine  = updated.IsProductionLine;
            rec.AdditionalRate    = updated.AdditionalRate;
            rec.StandardLaborCost = updated.StandardLaborCost;
            rec.MachineHourlyRate = updated.MachineHourlyRate;
            rec.StagingWhCode       = updated.StagingWhCode;
            rec.StagingLocationCode = updated.StagingLocationCode;
            rec.Remarks           = updated.Remarks;
            rec.IsActive          = updated.IsActive;
            rec.UpdatedAt         = DateTime.UtcNow;

            // 機台逐列比對（不再「全刪重建」）：
            //   - 既有機台：只更新名稱、備註。狀態一律不從這裡改——要經「機台狀態異動」，
            //     那裡才會處理派工（停用→已排退回待排、已投入轉暫停；恢復→自動恢復）並留下異動紀錄。
            //     原本在這裡改成故障／保養，機台上的派工完全不動，排程照樣排在一台不能用的機台上。
            //   - 新機台：可設定初始狀態（還沒有任何派工）。
            //   - 被移除的機台：尚有未結束的派工時不准刪，否則派工掛在一台不存在的機台上。
            // 生產線（IsProductionLine）也可掛列：每筆語義上是一條「線別」，排程視為平行資源
            var existingMachines = await _context.WorkCenterMachines
                .Where(m => m.WorkCenterCode == workCenterCode)
                .ToListAsync();
            var incoming = (machines ?? Array.Empty<WorkCenterMachine>())
                .Where(m => !string.IsNullOrWhiteSpace(m.MachineCode) && !string.IsNullOrWhiteSpace(m.MachineName))
                .GroupBy(m => m.MachineCode.Trim())
                .ToDictionary(g => g.Key, g => g.First());

            var removed = existingMachines.Where(e => !incoming.ContainsKey(e.MachineCode)).ToList();
            if (removed.Count > 0)
            {
                var removedCodes = removed.Select(r => r.MachineCode).ToList();
                var busy = await _context.Dispatches.AsNoTracking()
                    .Where(d => d.MachineCode != null && removedCodes.Contains(d.MachineCode)
                             && d.PlantId == rec.PlantId
                             && d.Status != DispatchStatus.Closed && d.Status != DispatchStatus.Cancelled)
                    .Select(d => d.MachineCode!)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync();
                if (busy.Count > 0)
                {
                    var msg = _localizer["MachineHasOpenDispatchCannotDelete", string.Join(", ", busy)].Value;
                    if (IsAjax) return Json(new { success = false, message = msg });
                    TempData["ErrorMessage"] = msg;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }
                _context.WorkCenterMachines.RemoveRange(removed);
            }

            foreach (var (code, m) in incoming)
            {
                var exist = existingMachines.FirstOrDefault(e => e.MachineCode == code);
                if (exist != null)
                {
                    exist.MachineName = m.MachineName.Trim();
                    exist.Remarks     = m.Remarks;
                }
                else
                {
                    _context.WorkCenterMachines.Add(new WorkCenterMachine
                    {
                        WorkCenterCode = workCenterCode,
                        MachineCode    = code,
                        MachineName    = m.MachineName.Trim(),
                        MachineStatus  = m.MachineStatus,
                        Remarks        = m.Remarks
                    });
                }
            }

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

        // POST: /WorkCenter/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string workCenterCode)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.WorkCenters.FindAsync(workCenterCode);
            if (rec != null)
            {
                // 途程工序、工單工序、機台異動明細、工作曆、計畫停機、部門實際成本、重工單皆以
                // WorkCenterCode（部分表命名為 WorkCenterId）鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免工作中心刪除後留下孤兒資料或派工/排程機制失效。
                if (await IsWorkCenterInUseAsync(workCenterCode))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["WorkCenterInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["WorkCenterInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.WorkCenters.Remove(rec);
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

        /// <summary>工作中心是否仍被途程工序、工單工序、機台異動明細、工作曆、計畫停機、部門實際成本或重工單引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsWorkCenterInUseAsync(string workCenterCode)
        {
            if (await _context.RoutingOperations.AnyAsync(o => o.WorkCenterId == workCenterCode))
                return true;
            if (await _context.WorkOrderOperations.AnyAsync(o => o.WorkCenterId == workCenterCode))
                return true;
            if (await _context.MachineStatusOrderLines.AnyAsync(l => l.WorkCenterCode == workCenterCode))
                return true;
            if (await _context.WorkCalendars.IgnoreQueryFilters().AnyAsync(w => w.WorkCenterId == workCenterCode))
                return true;
            if (await _context.WorkCalendarSegments.IgnoreQueryFilters().AnyAsync(s => s.WorkCenterId == workCenterCode))
                return true;
            if (await _context.PlannedDowntimes.IgnoreQueryFilters().AnyAsync(d => d.WorkCenterId == workCenterCode))
                return true;
            if (await _context.DepartmentActualCosts.IgnoreQueryFilters().AnyAsync(d => d.WorkCenterCode == workCenterCode))
                return true;
            if (await _context.ReworkMaterialDocs.IgnoreQueryFilters().AnyAsync(r => r.WorkCenterCode == workCenterCode))
                return true;
            return false;
        }

        // GET: /WorkCenter/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.WorkCenters.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.WorkCenterCode.ToLower().Contains(lower) ||
                    t.WorkCenterName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.WorkCenterCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.WorkCenter.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["WorkCenterCode"].Value, L["WorkCenterName"].Value, L["DepartmentId"].Value,
                L["ShiftGroupCode"].Value, L["IsProductionLine"].Value, L["AdditionalRate"].Value,
                L["StandardLaborCost"].Value, L["MachineHourlyRate"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.WorkCenterCode, t.WorkCenterName, t.DepartmentId, t.ShiftGroupCode,
                t.IsProductionLine, t.AdditionalRate, t.StandardLaborCost, t.MachineHourlyRate, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "WorkCenter" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "WorkCenter.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "WorkCenter.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "WorkCenter.json"),
            };
        }

        private string? GetEffectivePlantId(SmartFactory.MES.Models.OrganizationUnit unit, List<SmartFactory.MES.Models.OrganizationUnit> allUnits)
        {
            if (!string.IsNullOrEmpty(unit.PlantId)) return unit.PlantId;
            if (string.IsNullOrEmpty(unit.ParentId)) return null;
            var parent = allUnits.FirstOrDefault(u => u.Id == unit.ParentId);
            return parent == null ? null : GetEffectivePlantId(parent, allUnits);
        }
    }
}
