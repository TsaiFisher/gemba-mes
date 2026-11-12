using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Controllers
{
    [Authorize]
    public class MonthEndController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMonthEndService _monthEndService;
        private readonly INrvAssessmentService _nrvService;
        private readonly IFxRevaluationService _fxService;
        private readonly IEclService _eclService;
        private readonly IActualRateReestimationService _reestService;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDateTimeService _dateTimeService;
        private readonly IAccountingPeriodService _accountingPeriods;

        private const string MenuKey = "MonthEnd";

        public MonthEndController(
            ApplicationDbContext context,
            IMonthEndService monthEndService, INrvAssessmentService nrvService,
            IFxRevaluationService fxService, IEclService eclService,
            IActualRateReestimationService reestService,
            IPermissionService permissionService, IUserContext userContext,
            IStringLocalizer<ProjectZSharedResource> localizer,
            IDateTimeService dateTimeService,
            IAccountingPeriodService accountingPeriods)
        {
            _dateTimeService = dateTimeService;
            _context = context;
            _monthEndService = monthEndService; _nrvService = nrvService;
            _fxService = fxService; _eclService = eclService;
            _reestService = reestService;
            _permissionService = permissionService; _userContext = userContext;
            _localizer = localizer;
            _accountingPeriods = accountingPeriods;
        }

        /// <summary>部門實際費用單筆輸入（費率重估分子）。</summary>
        public record DeptCostInputDto(string WorkCenterCode, decimal ActualLaborCost, decimal ActualOverheadCost);

        // GET: /MonthEnd?periodId=202601
        public async Task<IActionResult> Index(string? periodId)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.PeriodId = periodId;
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.PeriodOptions = await _context.AccountingPeriods
                .AsNoTracking()
                .OrderByDescending(p => p.PeriodId)
                .Select(p => p.PeriodId)
                .ToListAsync();

            if (string.IsNullOrWhiteSpace(periodId)) return View((MonthEndChecklist?)null);

            // 期間的真實狀態要給畫面：〈取消月結〉原本綁在「清單步驟 8 已完成」，
            // 但期間可能是從「會計期間」畫面直接進 Closing 的，那時步驟 8 永遠不會是已完成（W4-F8）。
            ViewBag.PeriodStatus = (await _accountingPeriods.GetByIdAsync(
                periodId, _userContext.PlantId ?? ""))?.Status;

            // 步驟 4 的部門費用輸入框預先帶入「部門實際費用」畫面已存的本期資料。
            // 原本輸入框永遠是空的而且必填：畫面上存過的費用在這裡還得再打一次，
            // 而這裡送出的值會 upsert 覆蓋畫面存的——兩個入口寫同一份資料、互不知道對方。
            var plantId = _userContext.PlantId ?? "";
            ViewBag.DeptCostPrefill = string.Join("\n", (await _context.DepartmentActualCosts.AsNoTracking()
                    .Where(d => d.PeriodId == periodId && d.PlantId == plantId)
                    .OrderBy(d => d.WorkCenterCode)
                    .ToListAsync())
                .Select(d => FormattableString.Invariant($"{d.WorkCenterCode}:{d.ActualLaborCost:0.####}:{d.ActualOverheadCost:0.####}")));

            var checklist = await _monthEndService.GetChecklistAsync(periodId, plantId);

            // 「執行人」存的是 UserId（GUID），直接顯示對讀者沒有意義——與會計期間畫面同一套轉法。
            var userIds = checklist?.Items.Select(i => i.CompletedBy).Where(id => id != null).Distinct().ToList()
                          ?? new List<string?>();
            ViewBag.UserNames = await _context.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
            return View(checklist);
        }

        // POST：初始化月結清單
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Init(string periodId)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _monthEndService.InitChecklistAsync(periodId, _userContext.PlantId ?? "");
                return Json(new { success = true, message = _localizer["ChecklistInitialized"].Value });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：完成步驟
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteStep(string checklistId, int stepNo, string? notes)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _monthEndService.CompleteStepAsync(checklistId, stepNo, _userContext.UserId ?? "", notes);
                return Json(new { success = true, message = string.Format(_localizer["StepCompleted"].Value, stepNo) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：取消月結（步驟 8 已封鎖，但關帳前稽核沒過，要退回去修資料）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelClosing(string checklistId)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _monthEndService.CancelClosingAsync(checklistId, _userContext.UserId ?? "");
                return Json(new { success = true, message = _localizer["ClosingCancelled"].Value });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：執行 NRV 批次（步驟 7 觸發）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNrv(string periodId)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                var results = await _nrvService.RunAssessmentAsync(periodId, _userContext.PlantId ?? "");
                return Json(new { success = true, message = string.Format(_localizer["NrvBatchCompleted"].Value, results.Count), count = results.Count });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：執行 IAS 21 期末匯率重評（步驟 6 觸發）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunFxRevaluation(string periodId, [FromBody] Dictionary<string, decimal> periodEndRates)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                var results = await _fxService.RunRevaluationAsync(periodId, _userContext.PlantId ?? "", periodEndRates);
                return Json(new { success = true, message = string.Format(_localizer["FxRevBatchCompleted"].Value, results.Count), count = results.Count });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：執行工單實際費率期末重估（步驟 4 觸發；先 upsert 部門實際費用再回刷）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunActualRateReestimation(
            string periodId, [FromBody] List<DeptCostInputDto>? deptCosts)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                var plantId = _userContext.PlantId ?? "";

                // 1. upsert 部門實際費用（持久化，供稽核與重跑）
                if (deptCosts != null)
                {
                    foreach (var d in deptCosts)
                    {
                        var existing = await Db.DepartmentActualCosts.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(x => x.PeriodId == periodId
                                                   && x.PlantId == plantId
                                                   && x.WorkCenterCode == d.WorkCenterCode);
                        if (existing == null)
                            Db.DepartmentActualCosts.Add(new DepartmentActualCost
                            {
                                PeriodId = periodId,
                                WorkCenterCode = d.WorkCenterCode,
                                ActualLaborCost = d.ActualLaborCost,
                                ActualOverheadCost = d.ActualOverheadCost,
                                PlantId = plantId,
                            });
                        else
                        {
                            existing.ActualLaborCost = d.ActualLaborCost;
                            existing.ActualOverheadCost = d.ActualOverheadCost;
                            existing.UpdatedAt = DateTime.UtcNow;
                        }
                    }
                    await Db.SaveChangesAsync();
                }

                // 2. 執行重估回刷
                var r = await _reestService.RunAsync(periodId, plantId);
                return Json(new
                {
                    success = true,
                    message = string.Format(_localizer["ActualRateReestCompleted"].Value,
                                            r.WorkOrdersRecosted, r.WipSnapshots),
                    r.WorkOrdersRecosted, r.WipSnapshots,
                    r.LaborAdjustment, r.OverheadAdjustment, r.JournalId,
                });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：執行 ECL 計算（步驟 6 觸發）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunEcl(string periodId)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                // 結帳作業的基準日必須是廠別當地日：ECL 是期末科目，UTC 與當地日跨月時
                // 會把整批減損算到錯的期間。而且是**該期期末**，不是今天（見 EclService.AsOfFor）。
                var plantId   = _userContext.PlantId ?? "";
                var periodEnd = await _context.AccountingPeriods.AsNoTracking()
                    .Where(p => p.PeriodId == periodId && p.PlantId == plantId)
                    .Select(p => (DateOnly?)p.EndDate).FirstOrDefaultAsync();
                var asOfDate  = EclService.AsOfFor(periodEnd, _dateTimeService.GetLocalDate());
                var results   = await _eclService.ComputeEclAsync(periodId, plantId, asOfDate);
                return Json(new { success = true, message = string.Format(_localizer["EclBatchCompleted2"].Value, results.Count) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        // POST：略過步驟
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SkipStep(string checklistId, int stepNo, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                await _monthEndService.SkipStepAsync(checklistId, stepNo, _userContext.UserId ?? "", reason ?? "");
                return Json(new { success = true, message = string.Format(_localizer["StepCompleted"].Value, stepNo) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }
    }
}
