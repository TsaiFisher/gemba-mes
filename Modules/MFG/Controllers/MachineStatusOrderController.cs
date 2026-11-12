using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 機台狀態異動單（GTR）— 桌機 CRUD（mfg-domain.md §13）。
    /// 手機端異動由 MobileMfgController 處理；此 Controller 提供桌機維護、查詢與回顧功能。
    /// 副作用（Scheduled→Pending / InProgress→OnHold）由 MobileMfgController.MachineStatusEvent 負責；
    /// 桌機端建立時呼叫相同副作用邏輯。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class MachineStatusOrderController : ModuleControllerBase
    {
        private const string MenuKey = "MachineStatusOrder";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public MachineStatusOrderController(IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "event_at_desc",
            string searchText = "", string machineCode = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort    = sortOrder;
            ViewBag.SearchText     = searchText;
            ViewBag.MachineCode    = machineCode;
            ViewBag.Searched       = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<MachineStatusOrder>?)null);

            var q = Db.MachineStatusOrders.AsNoTracking()
                .Include(o => o.Lines)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchText))
                q = q.Where(o => o.OrderId.Contains(searchText) || o.Note != null && o.Note.Contains(searchText));
            if (!string.IsNullOrEmpty(machineCode))
                q = q.Where(o => o.Lines.Any(l => l.MachineCode == machineCode));

            q = sortOrder switch
            {
                "order_id"      => q.OrderBy(o => o.OrderId),
                "event_at"      => q.OrderBy(o => o.EventAt),
                _               => q.OrderByDescending(o => o.EventAt)
            };

            var list = await PaginatedList<MachineStatusOrder>.CreateAsync(q, pageNumber ?? 1, pageSize);

            var userIds = list.Select(o => o.RequestedBy).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            ViewBag.UserNames = await Db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            // 機台清單（供篩選）
            ViewBag.Machines = await Db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.WorkCenter!.PlantId == UserContext.PlantId)
                .OrderBy(m => m.MachineCode)
                .Select(m => new { m.MachineCode, m.MachineName })
                .ToListAsync();

            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var order = await Db.MachineStatusOrders
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderId == id);
            if (order == null) return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

            ViewBag.IsReadOnly = true;
            ViewBag.UserNames = string.IsNullOrEmpty(order.RequestedBy)
                ? new Dictionary<string, string>()
                : await Db.Users.AsNoTracking()
                    .Where(u => u.Id == order.RequestedBy)
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Details", order) : View("_Details", order);
        }

        [HttpGet]
        public async Task<IActionResult> CreateForm()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return PartialView("_Form", new MachineStatusOrder());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(string eventType, string machineCode, string? note, string? sourceDocId)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return JsonFail(_localizer["NoCreatePermission"].Value);

            if (!Enum.TryParse<MachineEventType>(eventType, out var evt))
                return JsonFail(_localizer["InvalidEventType"].Value);

            // 🚨 機台查詢必須帶廠別：WorkCenterMachine 不是 IPlantEntity（廠別掛在 WorkCenter 上），
            // 全域過濾攔不到，少了這個條件就會改到別廠機台的主檔狀態並開出跨廠的異動單。
            // 同一支控制器上方的機台**清單**（PrepareFormDropdowns／Index）本來就有這個條件，
            // 只有這個**寫入**點漏掉——於是下拉選不到的機台，直接送代碼還是改得動。
            var machine = await Db.WorkCenterMachines
                .FirstOrDefaultAsync(m => m.MachineCode == machineCode
                                       && m.WorkCenter!.PlantId == UserContext.PlantId);
            if (machine == null)
                return JsonFail(_localizer["MachineNotFound"].Value);

            var targetStatus = GetTargetStatus(evt, machine.MachineStatus);
            var now = DateTime.UtcNow;
            var orderId = Guid.NewGuid().ToString("N")[..12].ToUpper();

            var order = new MachineStatusOrder
            {
                OrderId     = orderId,
                EventType   = evt,
                EventAt     = now,
                RequestedBy = UserContext.UserId,
                Note        = note,
                PlantId     = UserContext.PlantId,
                CreatedAt   = now,
                UpdatedAt   = now
            };
            Db.MachineStatusOrders.Add(order);

            Db.MachineStatusOrderLines.Add(new MachineStatusOrderLine
            {
                OrderId        = orderId,
                LineSeq        = 1,
                MachineCode    = machineCode,
                WorkCenterCode = machine.WorkCenterCode,
                TargetStatus   = targetStatus,
                SourceDocId    = sourceDocId
            });

            // 更新機台狀態
            machine.MachineStatus = targetStatus;

            // §13.4 排程副作用：停用→已排退回待排、已投入轉暫停；恢復可用→因停機暫停的自動恢復。
            // 與手機 MachineStatusEvent、模具保養到期共用 DispatchLifecycleService。
            await HttpContext.RequestServices
                .GetRequiredService<IDispatchLifecycleService>()
                .ApplyMachineStatusAsync(machineCode, UserContext.PlantId, targetStatus, now);

            // 保養完成（HO＋SourceDocId=模具代號）→ 累計打次歸零（mfg-domain.md §14.1 第二階段）
            await HttpContext.RequestServices
                .GetRequiredService<IToolingShotService>()
                .ResetOnMaintenanceClosedAsync(evt, sourceDocId, now);

            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.Machines = await Db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.WorkCenter!.PlantId == UserContext.PlantId)
                .OrderBy(m => m.MachineCode)
                .Select(m => new { m.MachineCode, m.MachineName, m.WorkCenterCode, m.MachineStatus })
                .ToListAsync();
        }

        private static MachineStatus GetTargetStatus(MachineEventType evt, MachineStatus current) => evt switch
        {
            MachineEventType.CI => MachineStatus.Available,
            MachineEventType.II => MachineStatus.Standby,
            MachineEventType.DI => MachineStatus.BreakDown,
            MachineEventType.DO => MachineStatus.Available,
            MachineEventType.HI => MachineStatus.Maintenance,
            MachineEventType.HO => MachineStatus.Available,
            MachineEventType.EO => MachineStatus.Modification,
            MachineEventType.FO => MachineStatus.Loaned,
            MachineEventType.FI => MachineStatus.Available,
            MachineEventType.GO => MachineStatus.Retired,
            MachineEventType.CO => MachineStatus.ExternalRepair,
            _                   => current
        };
    }
}
