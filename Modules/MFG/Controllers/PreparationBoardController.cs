using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 生產前置準備看板（mfg-domain.md §23.3）。
    /// 資料來自既有 /api/mfg/preparation；此 Controller 僅提供 MVC 殼頁與權限守門。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class PreparationBoardController : ModuleControllerBase
    {
        private const string MenuKey = "PreparationBoard";

        private readonly IDateTimeService _dateTime;

        public PreparationBoardController(IDateTimeService dateTime)
        {
            _dateTime = dateTime;
        }

        // GET: /PreparationBoard
        public async Task<IActionResult> Index()
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            // GetLocalDate 的參數是時區代碼，不是廠別——原本傳 plantId，每開一次就記一筆
            // 「時區代碼『P1』無法解析」並退回預設時區。
            var tomorrow   = _dateTime.GetLocalDate().AddDays(1).ToString("yyyy-MM-dd");

            ViewBag.DefaultDate = tomorrow;

            // 工作中心清單（前端篩選用）
            ViewBag.WorkCenters = await Db.WorkCenters
                .AsNoTracking()
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            await PrepareIndexPermissionsAsync(MenuKey);
            return View();
        }
    }
}
