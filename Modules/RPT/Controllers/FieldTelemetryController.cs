using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    /// <summary>
    /// 現場遙測儀表板：手機回報的前端錯誤、API 耗時與失敗、斷線、各裝置還有多少資料沒送出。
    /// 資料由 <c>/api/telemetry/events</c> 收進來（見 <see cref="ClientTelemetryService"/>），本畫面唯讀。
    /// </summary>
    [Authorize]
    public class FieldTelemetryController : ModuleControllerBase
    {
        private readonly IFieldTelemetryReportService _report;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;

        private const string MenuKey = "FieldTelemetry";

        public FieldTelemetryController(IFieldTelemetryReportService report, IPermissionService permissionService,
            IUserContext userContext)
        {
            _report = report;
            _permissionService = permissionService;
            _userContext = userContext;
        }

        public async Task<IActionResult> Index(int hours = 24, string? kind = null)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var vm = await _report.BuildAsync(_userContext.PlantId, hours, kind, DateTime.UtcNow);
            return View(vm);
        }
    }
}
