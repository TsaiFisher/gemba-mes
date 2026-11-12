using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// PC 倉庫作業畫面（MSMC01 equivalent）。
    /// 畫面本身只負責渲染 tab 與呼叫 /api/warehouse/* 端點；
    /// 實際邏輯在 WarehouseApiController。
    /// </summary>
    [Authorize]
    public class WarehouseOpsController : ModuleControllerBase
    {
        private readonly IPermissionService _permissionService;
        private const string MenuKey = "WarehouseOps";

        public WarehouseOpsController(IPermissionService permissionService)
        {
            _permissionService = permissionService;
        }

        public async Task<IActionResult> Index()
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            return View();
        }
    }
}
