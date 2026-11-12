using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    [Authorize]
    public class DashboardController : ModuleControllerBase
    {
        private readonly IDashboardService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly ICurrencyService _currencyService;

        private const string MenuKey = "Dashboard";

        public DashboardController(
            IDashboardService service,
            IPermissionService permissionService,
            IUserContext userContext,
            ICurrencyService currencyService)
        {
            _service           = service;
            _permissionService = permissionService;
            _userContext       = userContext;
            _currencyService   = currencyService;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index()
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var kpi = await _service.GetKpiAsync(_userContext.PlantId ?? "");
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            return View(kpi);
        }
    }
}
