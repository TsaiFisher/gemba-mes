using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 產線即時看板（一鍵看整條線在忙什麼，2026-07-15）：全部生產線（WorkCenter.IsProductionLine）
    /// 一次總覽——目前有哪些線在生產/備料、線上有誰、目前派工進度。純即時快照，前端輪詢刷新。
    /// </summary>
    [Authorize]
    public class LineStatusController : ModuleControllerBase
    {
        private const string MenuKey = "LineStatus";
        private readonly LineStatusAnalyticsService _service;

        public LineStatusController(LineStatusAnalyticsService service)
        {
            _service = service;
        }

        public async Task<IActionResult> Index()
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            await PrepareIndexPermissionsAsync(MenuKey);
            return View();
        }

        [HttpGet]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Data()
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            var vm = await _service.BuildDashboardAsync(UserContext.PlantId);
            return JsonOk(vm);
        }
    }
}
