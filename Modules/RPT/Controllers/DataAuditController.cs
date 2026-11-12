using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    /// <summary>
    /// 資料稽核診斷儀表板（business_data_flow_reconciliation.md §9 P-8）。
    /// 呈現每日排程（DailyDataAuditJobService）持久化的 §3 稽核結果與趨勢，
    /// 並可手動觸發一次全廠稽核；異常在月結擋關之前即主動浮現。
    /// </summary>
    [Authorize]
    public class DataAuditController : ModuleControllerBase
    {
        private readonly IDataAuditReportService _report;
        private readonly IDataAuditRunService _runner;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDerivedFieldRepairService _repair;

        private const string MenuKey = "DataAudit";

        public DataAuditController(
            IDataAuditReportService report,
            IDataAuditRunService runner,
            IPermissionService permissionService,
            IUserContext userContext,
            IStringLocalizer<ProjectZSharedResource> localizer,
            IDerivedFieldRepairService repair)
        {
            _repair = repair;
            _report = report;
            _runner = runner;
            _permissionService = permissionService;
            _userContext = userContext;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(string? auditType)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            ViewBag.CanRunNow = await _permissionService.CanUpdate(MenuKey);

            var vm = await _report.BuildAsync(_userContext.PlantId ?? "", auditType);
            // 可自動修正的累計欄位：即時計算（不靠最近一次稽核的快照），畫面上看到的就是現在的差異
            ViewBag.RepairProposals = await _repair.PreviewAsync(_userContext.PlantId ?? "");
            return View(vm);
        }

        /// <summary>
        /// 修正勾選的累計欄位（需異動權限）。應為值在套用當下重新計算，不採信畫面上的數字；
        /// 每一筆的前後值與操作者由稽核紀錄留下。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RepairDerivedFields(string[]? keys)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoPermission"].Value);
            if (keys == null || keys.Length == 0)
                return JsonFail(_localizer["DataRepairNothingSelected"].Value);

            var r = await _repair.ApplyAsync(_userContext.PlantId ?? "", keys);
            return JsonOk(message: string.Format(_localizer["DataRepairDone"].Value, r.Applied, r.Unchanged, r.NotFound));
        }

        /// <summary>手動觸發一次全廠稽核並持久化結果（結果各廠自見所屬異常）。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow()
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoPermission"].Value);

            var run = await _runner.RunAndPersistAsync(
                plantId: null, triggerType: "Manual", createdBy: _userContext.UserId ?? "");
            return JsonOk(message: string.Format(
                _localizer["DataAuditRunDone"].Value, run.FailureCount));
        }
    }
}
