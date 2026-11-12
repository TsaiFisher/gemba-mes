using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.MFG;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 派工作業 — 查詢、上工（ProductionSession）、報工（ProductionReport）、
    /// Setup 備料、下工，以及成本自動累計（mfg-domain.md §4.3/§5.3/§16.3）。
    /// 並發衝突（DbUpdateConcurrencyException）回傳 HTTP 409（§6）。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class DispatchController : ModuleControllerBase
    {
        private const string MenuKey = "Dispatch";
        /// <summary>品質原因的暫停須有檢驗單更新權限才可恢復。</summary>
        private const string InspectionMenuKey = "InspectionOrder";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IInventoryReservationService _reservations;
        private readonly IPlantParamService _plantParams;
        private readonly IWorkOrderMaterialReadinessService _readiness;
        private readonly IProductionCostAccrualService _costAccrual;
        private readonly IStagingSourceService _stagingSource;
        private readonly Modules.BAS.Services.IUomService _uomService;
        private readonly IFpiGateService _fpiGate;
        private readonly IMfgSimplifiedModeService _simplified;
        private readonly IOperationSequenceGuard _sequenceGuard;

        public DispatchController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IInventoryReservationService reservations,
            IPlantParamService plantParams,
            IWorkOrderMaterialReadinessService readiness,
            IProductionCostAccrualService costAccrual,
            IStagingSourceService stagingSource,
            Modules.BAS.Services.IUomService uomService,
            IFpiGateService fpiGate,
            IMfgSimplifiedModeService simplifiedMode,
            IOperationSequenceGuard sequenceGuard)
        {
            _localizer     = localizer;
            _reservations  = reservations;
            _plantParams      = plantParams;
            _readiness     = readiness;
            _costAccrual   = costAccrual;
            _stagingSource = stagingSource;
            _uomService    = uomService;
            _fpiGate       = fpiGate;
            _simplified    = simplifiedMode;
            _sequenceGuard = sequenceGuard;
        }

        // ── 查詢 ──────────────────────────────────────────────────────────────

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "scheduled_start",
            string searchText = "", string statusFilter = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<DispatchListVm>?)null);

            // 派工前自動重排（廠別參數開啟且排程已過時才會跑）：現場看到的順序與機台是最新的
            await HttpContext.EnsureScheduleFreshAsync(UserContext.PlantId);

            var q = from d in Db.Dispatches.AsNoTracking()
                    join op in Db.WorkOrderOperations.AsNoTracking()
                        on new { d.WorkOrderId, d.OperationSeq } equals new { op.WorkOrderId, op.OperationSeq }
                        into opJoin from op in opJoin.DefaultIfEmpty()
                    join wo in Db.WorkOrders.AsNoTracking()
                        on d.WorkOrderId equals wo.WorkOrderId
                        into woJoin from wo in woJoin.DefaultIfEmpty()
                    join p in Db.Products.AsNoTracking()
                        on (wo != null ? wo.ProductCode : "") equals p.ProductCode
                        into pJoin from p in pJoin.DefaultIfEmpty()
                    select new DispatchListVm
                    {
                        DispatchId     = d.DispatchId,
                        WorkOrderId    = d.WorkOrderId,
                        ProductCode    = wo != null ? wo.ProductCode : "",
                        UomCode        = p != null ? p.StockUom : null,
                        OperationSeq   = d.OperationSeq,
                        OperationName  = op != null ? op.OperationName : null,
                        WorkCenterId   = op != null ? op.WorkCenterId : "",
                        MachineCode    = d.MachineCode,
                        Quantity       = d.Quantity,
                        QtyDone        = d.QtyDone,
                        Status         = d.Status,
                        IsOutsource    = d.IsOutsource,
                        ScheduledStart = d.ScheduledStart,
                        PlannedEndDate = wo != null ? (DateTime?)wo.PlannedEndDate : null,
                        ToolingCode    = d.ToolingCode,
                        HoldReason     = d.HoldReason,
                    };

            if (!string.IsNullOrEmpty(searchText))
                q = q.Where(v => v.DispatchId.Contains(searchText) ||
                                 v.WorkOrderId.Contains(searchText) ||
                                 v.ProductCode.Contains(searchText));

            if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<DispatchStatus>(statusFilter, out var st))
                q = q.Where(v => v.Status == st);
            else
                // 已取消（工單作廢、暫停後未產出即結案）不再是現場待辦；要看須明確選「已取消」
                q = q.Where(v => v.Status != DispatchStatus.Cancelled);

            q = sortOrder switch
            {
                "dispatch_id"       => q.OrderBy(v => v.DispatchId),
                "dispatch_id_desc"  => q.OrderByDescending(v => v.DispatchId),
                "work_order"        => q.OrderBy(v => v.WorkOrderId),
                "work_order_desc"   => q.OrderByDescending(v => v.WorkOrderId),
                "scheduled_start_desc" => q.OrderByDescending(v => v.ScheduledStart),
                _                   => q.OrderBy(v => v.ScheduledStart)
            };

            var list = await PaginatedList<DispatchListVm>.CreateAsync(q, pageNumber ?? 1, pageSize);

            // 模具保養警示（§14.1）：本頁派工引用的模具中，累計打次已達保養門檻者
            var toolingCodes = list.Where(v => !string.IsNullOrEmpty(v.ToolingCode))
                .Select(v => v.ToolingCode!).Distinct().ToList();
            if (toolingCodes.Count > 0)
            {
                var due = await Db.Toolings.AsNoTracking()
                    .Where(t => toolingCodes.Contains(t.ToolingCode)
                             && t.PmIntervalShots > 0 && t.AccumulatedShots >= t.PmIntervalShots)
                    .Select(t => t.ToolingCode)
                    .ToListAsync();
                foreach (var v in list)
                    v.ToolingPmDue = v.ToolingCode != null && due.Contains(v.ToolingCode);
            }

            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            return View(list);
        }

        /// <summary>派工明細（唯讀）：顯示 Sessions / Reports 歷史。</summary>
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var dispatch = await Db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);

            if (dispatch == null)
                return IsAjax
                    ? JsonFail(_localizer["RecordNotFound"].Value)
                    : NotFound();

            var op = await Db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId &&
                                          o.OperationSeq == dispatch.OperationSeq);

            var sessions = await Db.ProductionSessions.AsNoTracking()
                .Where(s => s.DispatchId == id)
                .OrderByDescending(s => s.StartAt)
                .ToListAsync();

            var sessionIds = sessions.Select(s => s.SessionId).ToList();
            var reports = await Db.ProductionReports.AsNoTracking()
                .Where(r => sessionIds.Contains(r.SessionId))
                .OrderByDescending(r => r.ReportedAt)
                .ToListAsync();

            var setups = await Db.ProductionSetups.AsNoTracking()
                .Where(s => s.DispatchId == id)
                .OrderByDescending(s => s.SetupStartAt)
                .ToListAsync();

            var operatorIds = sessions.Select(s => s.OperatorId)
                .Concat(setups.Select(s => s.OperatorId))
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .Distinct()
                .ToList();
            ViewBag.UserNames = operatorIds.Count == 0
                ? new Dictionary<string, string>()
                : await Db.Users.AsNoTracking()
                    .Where(u => operatorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            var wo = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            ViewBag.ProductUom = wo != null
                ? await Db.Products.AsNoTracking().Where(p => p.ProductCode == wo.ProductCode)
                    .Select(p => p.StockUom).FirstOrDefaultAsync()
                : null;
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            ViewBag.Operation = op;
            ViewBag.Sessions  = sessions;
            ViewBag.Reports   = reports;
            ViewBag.Setups    = setups;
            ViewBag.IsReadOnly = true;
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Details", dispatch) : View("_Details", dispatch);
        }

        // ── Setup 備料 ────────────────────────────────────────────────────────

        /// <summary>載入開工備料 Side Panel（GET）。</summary>
        public async Task<IActionResult> SetupForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var dispatch = await Db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return NotFound();

            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);

            var mySetup = await Db.ProductionSetups.AsNoTracking()
                .Where(s => s.DispatchId == id && s.Status == ProductionSetupStatus.Open
                            && s.OperatorId == UserContext.UserId)
                .FirstOrDefaultAsync();

            ViewBag.OpenSetup = mySetup;
            ViewBag.IsProductionLine = isProductionLine;

            // 備料＝把本派工要用的料開備料單移入備料倉（不再回報模具；模具沿用排程指定的
            // Dispatch.ToolingCode，打次與衝突偵測不受影響）。
            ViewBag.StagingOrders   = await _stagingSource.GetExistingOrdersAsync(
                "Dispatch", id, dispatch.PlantId ?? "");
            ViewBag.StagingMaterials = await _stagingSource.GetLinesAsync(
                "Dispatch", id, dispatch.PlantId ?? "");

            // 工序沒設準備工時＝免 Setup，畫面直接引導開工，不必為了流程而按一次備料
            var setupOp = await Db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId
                                       && o.OperationSeq == dispatch.OperationSeq);
            ViewBag.RequiresSetup = (setupOp?.SetupTime ?? 0) > 0;
            if (isProductionLine)
            {
                var activeOperatorIds = await Db.ProductionSetups.AsNoTracking()
                    .Where(s => s.DispatchId == id && s.Status == ProductionSetupStatus.Open)
                    .Select(s => s.OperatorId)
                    .Where(x => x != null)
                    .Distinct()
                    .ToListAsync();
                ViewBag.ActiveOperatorNames = activeOperatorIds.Count == 0
                    ? new List<string>()
                    : await Db.Users.AsNoTracking()
                        .Where(u => activeOperatorIds.Contains(u.Id))
                        .Select(u => u.UserName ?? u.Id)
                        .ToListAsync();
            }

            return PartialView("_SetupForm", dispatch);
        }

        /// <summary>
        /// 由本派工一鍵建立草稿備料單（帶入未發料用料＋預設備料倉）。
        /// 後續確認鎖料、揀料移入備料倉在備料單畫面完成。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateStagingOrder(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            try
            {
                var stagingId = await _stagingSource.CreateDraftAsync(
                    "Dispatch", id, dispatch.PlantId ?? "", UserContext.UserId ?? "");
                return Json(new
                {
                    success = true,
                    message = _localizer["StagingOrderCreated"].Value,
                    stagingId,
                });
            }
            catch (InvalidOperationException ex)
            {
                return JsonFail(ex.Message);
            }
        }

        /// <summary>
        /// 開始備料：Dispatch → SetupInProgress（mfg-domain.md §4.3 §5.3）。生產線允許多人同時備料。
        /// 模具不再由現場回報，一律沿用排程指定的 <c>Dispatch.ToolingCode</c>。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> StartSetup(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);

            // 生產線備料期間，其他人也可以加入（此時 Dispatch 已經是 SetupInProgress）；
            // 一般機台維持原本只能從 Scheduled/ReadyToStart 開始備料一次。
            var validEntryStatus = dispatch.Status == DispatchStatus.Scheduled
                || dispatch.Status == DispatchStatus.ReadyToStart
                || (isProductionLine && dispatch.Status == DispatchStatus.SetupInProgress);
            if (!validEntryStatus)
                return JsonFail(_localizer["DispatchInvalidStatusForSetup"].Value);

            var existing = isProductionLine
                ? await Db.ProductionSetups.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSetupStatus.Open && s.OperatorId == UserContext.UserId)
                : await Db.ProductionSetups.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSetupStatus.Open);
            if (existing)
                return JsonFail(_localizer["SetupAlreadyOpen"].Value);

            var now = DateTime.UtcNow;
            var setup = new ProductionSetup
            {
                SetupId      = Guid.NewGuid().ToString("N"),
                DispatchId   = dispatch.DispatchId,
                WorkOrderId  = dispatch.WorkOrderId,
                Status       = ProductionSetupStatus.Open,
                SetupStartAt = now,
                OperatorId   = UserContext.UserId,
                ToolingCode  = dispatch.ToolingCode,
                PlantId      = dispatch.PlantId,
                CreatedAt    = now,
                UpdatedAt    = now
            };
            Db.ProductionSetups.Add(setup);

            if (dispatch.Status != DispatchStatus.SetupInProgress)
            {
                dispatch.Status    = DispatchStatus.SetupInProgress;
                dispatch.UpdatedAt = now;
            }
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SetupStarted"].Value);
        }

        /// <summary>
        /// 備料完成：關閉這張派工單「所有」Open 的 ProductionSetup（可能有多人同時在備料）；
        /// Dispatch → ReadyToStart；累計 ActualOverheadCost 依「最早開始備料」到現在的時間窗算一次
        /// （不論一般機台 1 人或生產線多人，都是同一個算法：只有 1 筆時等同原行為）。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DoneSetup(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            if (dispatch.Status != DispatchStatus.SetupInProgress)
                return JsonFail(_localizer["DispatchNotInSetup"].Value);

            var openSetups = await Db.ProductionSetups
                .Where(s => s.DispatchId == id && s.Status == ProductionSetupStatus.Open)
                .ToListAsync();
            if (openSetups.Count == 0) return JsonFail(_localizer["SetupNotFound"].Value);

            var now = DateTime.UtcNow;
            foreach (var s in openSetups)
            {
                s.SetupEndAt = now;
                s.Status     = ProductionSetupStatus.Closed;
                s.UpdatedAt  = now;
            }

            // 累計 Setup 成本（mfg-domain.md §16.3）：人工逐人計、製造費用依時間窗計一次。
            // 與手機端 SetupDone 共用同一份邏輯，避免雙軌各改一邊。
            await _costAccrual.AccrueOnSetupDoneAsync(openSetups, dispatch);

            // §26 FPI 閘口：工序要求首件檢查且尚未通過 → 進 PendingFPI 等品管量測（與手機端同語意）。
            // 此狀態僅供待辦清單與 UI 顯示；真正擋開工的是 StartSession 的事實判定（§26.2-①）。
            var fpi = await _fpiGate.EvaluateAsync(dispatch);
            dispatch.Status    = fpi.IsBlocked ? DispatchStatus.PendingFPI : DispatchStatus.ReadyToStart;
            dispatch.UpdatedAt = now;
            await Db.SaveChangesAsync();

            // R1：Localizer 索引一律靜態字面，不在索引子內做條件運算
            var setupMsg = fpi.IsBlocked
                ? _localizer["SetupDonePendingFpi"].Value
                : _localizer["SetupDone"].Value;
            return JsonOk(data: new { requiresFpi = fpi.IsBlocked }, message: setupMsg);
        }

        // ── 上工 / 下工 ───────────────────────────────────────────────────────

        /// <summary>載入開工 Side Panel（GET）。</summary>
        public async Task<IActionResult> SessionForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var dispatch = await Db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return NotFound();

            // 生產線（WorkCenter.IsProductionLine）可多人同時上工：面板要判斷「我自己」有沒有在線上，
            // 不是「這張派工單有沒有任何人」在線上，否則其他人已上工時自己會被誤判成也在生產中。
            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);

            var mySession = await Db.ProductionSessions.AsNoTracking()
                .Where(s => s.DispatchId == id && s.Status == ProductionSessionStatus.Open
                            && s.OperatorId == UserContext.UserId)
                .FirstOrDefaultAsync();

            ViewBag.OpenSession = mySession;
            ViewBag.IsProductionLine = isProductionLine;
            if (isProductionLine)
            {
                var activeOperatorIds = await Db.ProductionSessions.AsNoTracking()
                    .Where(s => s.DispatchId == id && s.Status == ProductionSessionStatus.Open)
                    .Select(s => s.OperatorId)
                    .Where(x => x != null)
                    .Distinct()
                    .ToListAsync();
                ViewBag.ActiveOperatorNames = activeOperatorIds.Count == 0
                    ? new List<string>()
                    : await Db.Users.AsNoTracking()
                        .Where(u => activeOperatorIds.Contains(u.Id))
                        .Select(u => u.UserName ?? u.Id)
                        .ToListAsync();
            }

            // 機台／模具改用下拉（廠別設施類欄位不得以自由文字輸入）。
            ViewBag.MachineOptions = await Db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.WorkCenter!.PlantId == UserContext.PlantId
                            && m.MachineStatus != MachineStatus.Retired)
                .OrderBy(m => m.MachineCode)
                .Select(m => new { m.MachineCode, m.MachineName })
                .ToListAsync();
            ViewBag.ToolingOptions = await Db.Toolings.AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.ToolingCode)
                .Select(t => new { t.ToolingCode, t.ToolingName })
                .ToListAsync();

            return PartialView("_SessionForm", dispatch);
        }

        /// <summary>開工：建立 ProductionSession；Dispatch → InProgress；WorkOrder → InProgress（mfg-domain.md §5.3）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> StartSession(string id, string? machineCode, string? toolingCode)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            // §25.4 簡化模式額外放行 Pending（免排程 → 派工不會被推進 Scheduled）；
            // 放行清單由 IMfgSimplifiedModeService 統一判定，與手機端共用同一份。
            if (!await _simplified.CanStartSessionAsync(dispatch.Status, UserContext.PlantId))
                return JsonFail(_localizer["DispatchCannotStart"].Value);

            // §18.2 投產前審核：工單須已投產／生產中，且非送審中／被退回
            switch (await WorkOrderApprovalGate.CheckStartAsync(Db, dispatch.WorkOrderId))
            {
                case WorkOrderStartBlock.NotReleased: return JsonFail(_localizer["WorkOrderNotReleasedCannotStart"].Value);
                case WorkOrderStartBlock.NotApproved: return JsonFail(_localizer["WorkOrderNotApprovedCannotStart"].Value);
            }

            // §26 首件檢查閘口：以「工序要求 FPI 且尚無 Pass 檢驗單」為準，不以派工狀態為準——
            // 舊版只擋 PendingFPI，而該狀態只能經備料進入，不做備料直接開工即可繞過。
            // 僅在首次開工時檢查，續工（已 InProgress）不重複阻擋。
            if (dispatch.Status != DispatchStatus.InProgress
                && (await _fpiGate.EvaluateAsync(dispatch)).IsBlocked)
                return JsonFail(_localizer["DispatchPendingFpi"].Value);

            // §21.5 物料備齊閘口：依系統參數 MFG_MATERIAL_GATE 檢查。
            //   None  → 不檢查；Block → 缺料阻擋開工；Warning（預設）→ 缺料仍可開工但回傳警示。
            // 僅在首次開工（尚未 InProgress）時檢查，避免續工被重複阻擋。
            string? startWarning = null;
            if (dispatch.Status != DispatchStatus.InProgress)
            {
                var gate = (await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgMaterialGate, UserContext.PlantId) ?? "Warning").Trim();
                if (!string.Equals(gate, "None", StringComparison.OrdinalIgnoreCase))
                {
                    var readiness = await _readiness.CheckAsync(dispatch.WorkOrderId);
                    if (readiness.Overall == MaterialReadinessStatus.Shortage)
                    {
                        if (string.Equals(gate, "Block", StringComparison.OrdinalIgnoreCase))
                            return JsonFail(_localizer["DispatchMaterialShortageBlocked"].Value);
                        startWarning = _localizer["DispatchMaterialShortageWarning"].Value;
                    }
                }
            }

            // 改選的機台會回寫到派工（見 SyncDispatchToActualMachineAsync），進而影響排程佔用，
            // 故須是本廠、非廢棄的機台；下拉本來就只列這些，這裡擋的是手改送出的值。
            // 與換機台（ReassignMachine）一致，允許跨工作中心。
            if (!string.IsNullOrWhiteSpace(machineCode) && machineCode != dispatch.MachineCode
                && !await Db.WorkCenterMachines.AnyAsync(m => m.MachineCode == machineCode
                        && m.WorkCenter!.PlantId == dispatch.PlantId && m.MachineStatus != MachineStatus.Retired))
                return JsonFail(_localizer["MachineNotFound"].Value);

            // 機台狀態閘口（MfgMachineStatusGate，預設 Warning）：故障、保養中等不可排程狀態的機台
            // 擋下或警示開工。與排程共用 MachineAvailability。續工也檢查——機台中途報修後不該再有人上線。
            var machineCheck = await MachineAvailability.CheckStartAsync(
                Db, _plantParams, dispatch.PlantId, machineCode ?? dispatch.MachineCode);
            if (machineCheck.Blocked)
                return JsonFail(_localizer["DispatchMachineUnavailableBlocked",
                    _localizer["MachineStatus" + machineCheck.Status].Value].Value);
            if (machineCheck.Warned)
            {
                var machineWarning = _localizer["DispatchMachineUnavailableWarning",
                    _localizer["MachineStatus" + machineCheck.Status].Value].Value;
                startWarning = startWarning == null ? machineWarning : startWarning + " " + machineWarning;
            }

            // 生產線（WorkCenter.IsProductionLine）本來就是多人共同作業的連續產線，允許同時多筆
            // Open session；一般機台維持「同時只能 1 人在線上」（避免同一台機台被兩人各自宣稱操作）。
            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);
            var existing = isProductionLine
                ? await Db.ProductionSessions.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSessionStatus.Open && s.OperatorId == UserContext.UserId)
                : await Db.ProductionSessions.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSessionStatus.Open);
            if (existing)
                return JsonFail(_localizer["SessionAlreadyOpen"].Value);

            var now = DateTime.UtcNow;
            var session = new ProductionSession
            {
                SessionId   = Guid.NewGuid().ToString("N"),
                DispatchId  = dispatch.DispatchId,
                WorkOrderId = dispatch.WorkOrderId,
                Status      = ProductionSessionStatus.Open,
                StartAt     = now,
                OperatorId  = UserContext.UserId,
                MachineCode = machineCode ?? dispatch.MachineCode,
                ToolingCode = toolingCode,
                PlantId     = dispatch.PlantId,
                CreatedAt   = now,
                UpdatedAt   = now
            };
            Db.ProductionSessions.Add(session);

            await MachineAvailability.SyncDispatchToActualMachineAsync(Db, dispatch, machineCode, now);

            if (dispatch.Status != DispatchStatus.InProgress)
            {
                dispatch.Status    = DispatchStatus.InProgress;
                dispatch.UpdatedAt = now;

                // WorkOrder → InProgress（mfg-domain.md §4.2 InProgress 觸發條件）
                var wo = await Db.WorkOrders
                    .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
                if (wo != null && wo.Status == WorkOrderStatus.Released)
                {
                    wo.Status    = WorkOrderStatus.InProgress;
                    wo.UpdatedAt = now;
                }

                // §10/§21 提早投產：重算受影響同物料後續需求的 InventoryReservation（mfg-domain.md §5 業務規則 5）
                await RecalcReservationsOnEarlyStartAsync(dispatch.WorkOrderId, dispatch.PlantId);
            }

            await Db.SaveChangesAsync();
            return startWarning == null
                ? JsonOk(message: _localizer["SessionStarted"].Value)
                : Json(new { success = true, message = _localizer["SessionStarted"].Value, warning = startWarning });
        }

        /// <summary>下工：關閉 ProductionSession；累計 ActualLaborCost / ActualOverheadCost（§16.3）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> StopSession(string sessionId)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var session = await Db.ProductionSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (session == null) return JsonFail(_localizer["SessionNotFound"].Value);

            if (session.Status != ProductionSessionStatus.Open)
                return JsonFail(_localizer["SessionNotOpen"].Value);

            var now = DateTime.UtcNow;
            session.EndAt     = now;
            session.Status    = ProductionSessionStatus.Closed;
            session.UpdatedAt = now;

            // 累計實際人工/機台成本（mfg-domain.md §16.3；生產線多人上工時 overhead 防灌水規則見服務內註解）
            await _costAccrual.AccrueOnSessionStopAsync(session);

            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SessionStopped"].Value);
        }

        // ── 報工 ──────────────────────────────────────────────────────────────

        /// <summary>載入報工 Side Panel（GET）。</summary>
        public async Task<IActionResult> ReportForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var dispatch = await Db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return NotFound();

            // 生產線可能同時有多筆 Open session：優先抓「目前登入者自己」那筆，
            // 抓不到（例如主管代為報工）才退回抓任一筆，維持原本單人模式的相容行為。
            var openSession = await Db.ProductionSessions.AsNoTracking()
                .Where(s => s.DispatchId == id && s.Status == ProductionSessionStatus.Open
                            && s.OperatorId == UserContext.UserId)
                .FirstOrDefaultAsync()
                ?? await Db.ProductionSessions.AsNoTracking()
                    .Where(s => s.DispatchId == id && s.Status == ProductionSessionStatus.Open)
                    .FirstOrDefaultAsync();

            ViewBag.OpenSession = openSession;
            ViewBag.OperatorName = openSession?.OperatorId == null
                ? null
                : await Db.Users.AsNoTracking()
                    .Where(u => u.Id == openSession.OperatorId)
                    .Select(u => u.UserName)
                    .FirstOrDefaultAsync() ?? openSession.OperatorId;

            // 模具保養警示（§14.1）：報工面板提示作業員模具已達保養門檻
            ViewBag.ToolingPmDue = !string.IsNullOrEmpty(dispatch.ToolingCode) &&
                await Db.Toolings.AsNoTracking().AnyAsync(t =>
                    t.ToolingCode == dispatch.ToolingCode &&
                    t.PmIntervalShots > 0 && t.AccumulatedShots >= t.PmIntervalShots);

            var reportWo = await Db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            ViewBag.ProductUom = reportWo != null
                ? await Db.Products.AsNoTracking().Where(p => p.ProductCode == reportWo.ProductCode)
                    .Select(p => p.StockUom).FirstOrDefaultAsync()
                : null;
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            // 不良原因（CodeEntry: ng_reason）：報工填了不良數才需要選，語系由 extension 解析
            ViewBag.NgReasonOptions = await Db.CodeEntries
                .AsNoTracking()
                .Where(e => e.Category == "ng_reason" && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .ToDisplayOptionsAsync();

            // 容器（container-tracking.md §8 L1）：從容器維護選，預設＝該工序設定的完工容器
            ViewBag.ContainerOptions = await Db.Containers.AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.ContainerCode)
                .ToListAsync();
            ViewBag.DefaultContainerCode = await ReportContainers.OperationOutContainerAsync(
                Db, dispatch.WorkOrderId, dispatch.OperationSeq);

            return PartialView("_ReportForm", dispatch);
        }

        /// <summary>
        /// 報工：建立 ProductionReport，累計 Dispatch.QtyDone，自動完工（mfg-domain.md §5.3）。
        /// 以 RowVersion 樂觀並發控制，衝突時回 HTTP 409（§6）。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateReport(
            string id, string sessionId,
            decimal qtyOk, decimal qtyNg,
            string? containerCode, string? ngReasonCode = null, string? containerUnitId = null)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            if (dispatch.Status != DispatchStatus.InProgress)
                return JsonFail(_localizer["DispatchNotInProgress"].Value);

            (qtyOk, qtyNg) = await RoundReportQtyAsync(dispatch, qtyOk, qtyNg);

            var now   = DateTime.UtcNow;
            var error = await AddReportCoreAsync(dispatch, sessionId, qtyOk, qtyNg, containerCode, ngReasonCode, containerUnitId, now);
            if (error != null) return JsonFail(error);

            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                ConcurrencyConflictLog.Desktop(HttpContext, "CreateReport", dispatch.DispatchId);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return JsonFail(_localizer["ConcurrencyConflict"].Value);
            }

            QueueEtaRecalculate(dispatch, now);
            return JsonOk(message: _localizer["ReportCreated"].Value);
        }

        /// <summary>
        /// 完工：結束「本次上工」——可一併回報最後一批數量（未填則純下工）。
        /// 報工與完工刻意分成兩個動作：批次轉移的現場每轉一批就報一次工、但人還在線上繼續生產，
        /// 只有按完工才關閉 session 並結算工時成本。派工是否結案仍由累計數量達標自動判定，
        /// 因此生產線多人時各自完工、各報各的量，最後一人完工且數量達標才結案。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteSession(
            string id, string sessionId,
            decimal qtyOk = 0, decimal qtyNg = 0,
            string? containerCode = null, string? ngReasonCode = null, string? containerUnitId = null)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);

            var session = await Db.ProductionSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.DispatchId == id);
            if (session == null) return JsonFail(_localizer["SessionNotFound"].Value);
            if (session.Status != ProductionSessionStatus.Open)
                return JsonFail(_localizer["SessionNotOpen"].Value);

            // 先捨入再判斷：0.4 在整數單位下是 0，應視為純下工而不是被報工守門擋下
            (qtyOk, qtyNg) = await RoundReportQtyAsync(dispatch, qtyOk, qtyNg);

            var now = DateTime.UtcNow;

            // 有填數量才建報工紀錄；純下工（本次產出已在中途報完）不強迫再報一次 0
            if (qtyOk > 0 || qtyNg > 0)
            {
                if (dispatch.Status != DispatchStatus.InProgress)
                    return JsonFail(_localizer["DispatchNotInProgress"].Value);

                var error = await AddReportCoreAsync(dispatch, sessionId, qtyOk, qtyNg, containerCode, ngReasonCode, containerUnitId, now);
                if (error != null) return JsonFail(error);
            }

            session.EndAt     = now;
            session.Status    = ProductionSessionStatus.Closed;
            session.UpdatedAt = now;

            // 實際人工/機台成本（§16.3；生產線多人 overhead 防灌水規則見服務內註解）
            await _costAccrual.AccrueOnSessionStopAsync(session);

            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                ConcurrencyConflictLog.Desktop(HttpContext, "CompleteSession", dispatch.DispatchId);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return JsonFail(_localizer["ConcurrencyConflict"].Value);
            }

            QueueEtaRecalculate(dispatch, now);
            return JsonOk(message: _localizer["SessionCompleted"].Value);
        }

        /// <summary>
        /// 建立報工紀錄並累計數量／模具打次（不 SaveChanges，交由呼叫端與其他異動同交易提交）。
        /// 回傳錯誤訊息，null 代表成功。報工與完工兩條入口共用，避免守門條件各寫一份而漂移。
        /// </summary>
        private async Task<string?> AddReportCoreAsync(
            Dispatch dispatch, string sessionId,
            decimal qtyOk, decimal qtyNg,
            string? containerCode, string? ngReasonCode, string? containerUnitId, DateTime now)
        {
            // 任一數量為負一律拒絕（混合正負會穿透下方「皆非正」守門並倒扣 QtyDone）
            if (qtyOk < 0 || qtyNg < 0) return _localizer["ReportQtyRequired"].Value;
            if (qtyOk <= 0 && qtyNg <= 0) return _localizer["ReportQtyRequired"].Value;

            // 超量報工（mfg-domain.md §4.6）：廠別容許率內照收，超過依參數拒收或限主管（超量核准權限）。
            // 單元測試直接 new controller 時沒有 RequestServices——沿用原本的硬擋。
            if (dispatch.QtyDone + qtyOk > dispatch.Quantity)
            {
                var policy = HttpContext?.RequestServices?.GetService<IOverQuantityPolicy>();
                if (policy == null) return _localizer["OverReport"].Value;
                var over = await policy.CheckProductionAsync(dispatch.PlantId, UserContext.ActiveRoleId,
                    dispatch.Quantity, dispatch.QtyDone, qtyOk);
                if (!over.Allowed) return over.Message;
            }

            // §25.4 工序順序守門（僅簡化模式生效）：免排程後可能第 3 工序先報工，
            // 產出「後製程做得比前製程多」的不可能資料。判定與手機報工共用同一份實作。
            if ((await _sequenceGuard.CheckAsync(dispatch, qtyOk)).Blocked)
                return _localizer["ReportPrevOperationShortage"].Value;

            var session = await Db.ProductionSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.DispatchId == dispatch.DispatchId);
            if (session == null) return _localizer["SessionNotFound"].Value;

            // 掃了箱號（§9.4 生產中容器）：裝箱，容器種類跟著箱子走。箱子不能裝就整筆不報，
            // 免得實物裝進去了、系統卻沒記到（桌機是即時操作，擋下來當場就能換箱）
            string? boxId = null;
            if (!string.IsNullOrWhiteSpace(containerUnitId) && qtyOk > 0)
            {
                var (box, boxError) = await ReportContainers.LoadBoxAsync(
                    Db, containerUnitId, dispatch, qtyOk, UserContext.UserId, now);
                if (boxError != null) return boxError;
                if (box == null) return PzText.Get("Bas_ContainerUnitNotFoundFmt", containerUnitId.Trim());
                containerCode = box.ContainerCode;
                boxId         = box.ContainerUnitId;
            }

            // 桌機是下拉選單，不在容器維護裡（或已停用）的代碼只可能是竄改或畫面開著時被停用
            containerCode = string.IsNullOrWhiteSpace(containerCode) ? null : containerCode.Trim();
            if (containerCode != null && !await ReportContainers.IsActiveAsync(Db, containerCode))
                return _localizer["ContainerNotFound"].Value;

            var report = new ProductionReport
            {
                ReportId      = Guid.NewGuid().ToString("N"),
                SessionId     = sessionId,
                DispatchId    = dispatch.DispatchId,
                WorkOrderId   = dispatch.WorkOrderId,
                Status        = ProductionReportStatus.Reported,
                QtyOk         = qtyOk,
                QtyNg         = qtyNg,
                NgReasonCode  = qtyNg > 0 ? ngReasonCode : null,
                ContainerCode = containerCode,
                ReportedAt    = now,
                PlantId       = dispatch.PlantId,
                CreatedAt     = now,
                UpdatedAt     = now
            };
            await ReportContainers.AttachAsync(Db, report, boxId);
            Db.ProductionReports.Add(report);

            dispatch.QtyDone   += qtyOk;
            dispatch.UpdatedAt  = now;

            // 累計完工 → 自動 Closed（§4.3）
            if (dispatch.QtyDone >= dispatch.Quantity)
            {
                dispatch.Status = DispatchStatus.Closed;
                await CheckWorkOrderCompletionAsync(dispatch.WorkOrderId, now);
            }

            // 模具打次回寫＋保養門檻檢查（§14.1 第二階段），與報工同交易
            await HttpContext.RequestServices
                .GetRequiredService<IToolingShotService>()
                .AccumulateAsync(dispatch, qtyOk + qtyNg, now);

            return null;
        }

        /// <summary>
        /// 報工數量依產品庫存單位的小數位數四捨五入（與報工面板顯示用的是同一個單位）。
        /// 在任何守門之前做，超量／皆為 0 的判定才會和實際寫入的值一致。手機端 MobileMfgController 同一條。
        /// </summary>
        private async Task<(decimal QtyOk, decimal QtyNg)> RoundReportQtyAsync(Dispatch dispatch, decimal qtyOk, decimal qtyNg)
        {
            var uom = await Db.WorkOrders.AsNoTracking()
                .Where(w => w.WorkOrderId == dispatch.WorkOrderId)
                .Join(Db.Products.AsNoTracking(), w => w.ProductCode, p => p.ProductCode, (w, p) => p.StockUom)
                .FirstOrDefaultAsync();
            return (await _uomService.RoundQtyAsync(uom, qtyOk), await _uomService.RoundQtyAsync(uom, qtyNg));
        }

        /// <summary>報工／完工後背景觸發 ETA 重算 + Push（與手機端同一條 IReportEtaNotifier 管線）。</summary>
        private void QueueEtaRecalculate(Dispatch dispatch, DateTime now)
        {
            if (dispatch.PlantId == null) return;
            HttpContext.RequestServices
                .GetRequiredService<IReportEtaNotifier>()
                .QueueRecalculate(dispatch.DispatchId, dispatch.MachineCode, dispatch.PlantId, now);
        }

        // ── 私有輔助 ─────────────────────────────────────────────────────────

        /// <summary>此派工單所屬工作中心是否為生產線（<c>WorkCenter.IsProductionLine</c>），允許多人同時上工。</summary>
        private async Task<bool> IsProductionLineDispatchAsync(Dispatch dispatch)
        {
            var op = await Db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq);
            if (op?.WorkCenterId == null) return false;

            return await Db.Set<WorkCenter>().AsNoTracking()
                .Where(w => w.WorkCenterCode == op.WorkCenterId)
                .Select(w => w.IsProductionLine)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// 當派工 Closed 後，檢查該工單所有派工是否全部完工（mfg-domain.md §4.2 Completed 條件）。
        /// </summary>
        private async Task CheckWorkOrderCompletionAsync(string workOrderId, DateTime now)
        {
            // 注意：此方法在 CreateReport 的 SaveChangesAsync 之前被呼叫，當前派工的 Closed
            // 狀態尚未存檔。必須走「已追蹤實體」（不可 AsNoTracking，且不可投影 Select(d => d.Status)，
            // 投影會繞過 identity map 直接讀 DB 回舊值），讓 EF identity resolution 對當前派工
            // 回傳記憶體中的 Closed 狀態——否則最後一張派工完工時工單永遠不會自動轉 Completed。
            var allDispatches = await Db.Dispatches
                .Where(d => d.WorkOrderId == workOrderId)
                .ToListAsync();

            // 暫停不算完成（剩餘量要恢復生產或由生管結案）；取消算結束。判定與手機端、剩餘量結案共用。
            if (!DispatchHold.IsWorkOrderDone(allDispatches)) return;

            var wo = await Db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId);
            if (wo == null) return;

            // 只有生產中才自動轉完工（避免重複觸發）
            if (wo.Status == WorkOrderStatus.InProgress)
            {
                wo.Status    = WorkOrderStatus.Completed;
                wo.UpdatedAt = now;

                // Step 2：發佈 WorkOrderCompleted 事件（非同步副作用）
                // 背景服務訂閱此事件，執行成本累計 + 完工入庫異動
                var completedEvent = new WorkOrderCompletedEvent(
                    workOrderId: wo.WorkOrderId,
                    productCode: wo.ProductCode,
                    plantId: wo.PlantId,
                    standardLaborCost: wo.StandardLaborCost,
                    standardOverheadCost: wo.StandardOverheadCost);

                var eventPublisher = HttpContext.RequestServices.GetService<IDomainEventPublisher>();
                if (eventPublisher != null)
                {
                    eventPublisher.Publish(completedEvent);
                }
            }
        }

        // ── 例外調度（Phase C §10-7/§10-8）────────────────────────────────────

        /// <summary>
        /// 換機台（Pending/Scheduled 可換，釋放既有時段，等待下次重排）。
        /// 換過的機台會鎖定（IsMachineLocked）：重排只排在這台，不再被改回同工作中心最早可用的機台。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ReassignMachine(string id, string newMachineCode)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            if (dispatch.Status != DispatchStatus.Pending && dispatch.Status != DispatchStatus.Scheduled)
                return Json(new { success = false, message = _localizer["DispatchCannotAdjustStatus"].Value });

            if (string.IsNullOrWhiteSpace(newMachineCode))
                return Json(new { success = false, message = _localizer["MachineCodeRequired"].Value });

            // 機台須確實存在且非廢棄（Retired＝永久廢棄，不可指派）。
            // 業務規則允許跨工作中心調機，故不限定本工序工作中心，只驗證機台有效性。
            var targetMachine = newMachineCode.Trim();
            var machineUsable = await Db.WorkCenterMachines
                .AnyAsync(m => m.MachineCode == targetMachine && m.MachineStatus != MachineStatus.Retired);
            if (!machineUsable)
                return Json(new { success = false, message = _localizer["MachineNotFound"].Value });

            dispatch.MachineCode     = targetMachine;
            dispatch.IsMachineLocked = true;
            dispatch.ScheduledStart  = null;
            dispatch.ScheduledEnd    = null;
            dispatch.MaterialRisk    = false;
            dispatch.Status          = DispatchStatus.Pending;
            dispatch.UpdatedAt       = DateTime.UtcNow;
            await Db.SaveChangesAsync();

            return Json(new { success = true, message = _localizer["ReassignMachineSuccess"].Value });
        }

        // ── 暫停處理：恢復／換機續做／剩餘量結案 ─────────────────────────────────

        /// <summary>暫停處理側板（GET）：顯示暫停原因、恢復、換機續做、剩餘量結案。</summary>
        [HttpGet]
        public async Task<IActionResult> HoldForm(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var dispatch = await Db.Dispatches.AsNoTracking().FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return NotFound();

            // 換機續做可選：本廠、可生產（正常／待機）的機台，原機台除外。允許跨工作中心（與換機台一致）；
            // 同工作中心的排前面。
            var wcId = await Db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq)
                .Select(o => o.WorkCenterId)
                .FirstOrDefaultAsync();
            ViewBag.SwitchMachines = await Db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.WorkCenter!.PlantId == dispatch.PlantId
                         && m.MachineCode != dispatch.MachineCode
                         && (m.MachineStatus == MachineStatus.Available || m.MachineStatus == MachineStatus.Standby))
                .OrderBy(m => m.WorkCenterCode == wcId ? 0 : 1).ThenBy(m => m.MachineCode)
                .Select(m => new HoldSwitchMachineOption(m.MachineCode, m.MachineName, m.WorkCenterCode))
                .ToListAsync();

            ViewBag.MachineStatus = dispatch.MachineCode == null ? null
                : await Db.WorkCenterMachines.AsNoTracking()
                    .Where(m => m.MachineCode == dispatch.MachineCode && m.WorkCenter!.PlantId == dispatch.PlantId)
                    .Select(m => (MachineStatus?)m.MachineStatus)
                    .FirstOrDefaultAsync();
            ViewBag.CanReleaseQuality = await Permissions.CanUpdate(InspectionMenuKey);

            return PartialView("_HoldForm", dispatch);
        }

        /// <summary>
        /// 恢復暫停的派工（回到暫停前狀態；首件不合格者回到等待首件檢查）。
        /// 品質原因（首件／巡檢不合格）的暫停須有檢驗單更新權限——品管確認後才能放行。
        /// 機台停用造成的暫停，機台恢復可用時會自動解除；機台仍停用時不可手動恢復（改用換機續做）。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Resume(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (dispatch.Status != DispatchStatus.OnHold) return JsonFail(_localizer["DispatchNotOnHold"].Value);

            if (dispatch.HoldReason is DispatchHoldReason.FpiFailed or DispatchHoldReason.IpqcFailed
                && !await Permissions.CanUpdate(InspectionMenuKey))
                return JsonFail(_localizer["DispatchResumeNeedsQuality"].Value);

            if (dispatch.MachineCode != null)
            {
                var machineStatus = await Db.WorkCenterMachines.AsNoTracking()
                    .Where(m => m.MachineCode == dispatch.MachineCode && m.WorkCenter!.PlantId == dispatch.PlantId)
                    .Select(m => (MachineStatus?)m.MachineStatus)
                    .FirstOrDefaultAsync();
                if (machineStatus is { } ms && DispatchHold.IsMachineDown(ms))
                    return JsonFail(_localizer["DispatchResumeMachineDown"].Value);
            }

            var lifecycle = HttpContext.RequestServices.GetRequiredService<IDispatchLifecycleService>();
            await lifecycle.ResumeAsync(dispatch, DateTime.UtcNow);
            return await SaveOrConflictAsync(_localizer["DispatchResumed"].Value);
        }

        /// <summary>
        /// 換機續做：暫停的派工改到另一台機台繼續生產。已有產出時原派工以已完成量結案、剩餘量另開續做派工。
        /// 續做派工鎖定新機台、狀態為待排——要重新換模與首件檢查，排程重排時只排在新機台。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SwitchMachine(string id, string newMachineCode)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (dispatch.Status != DispatchStatus.OnHold) return JsonFail(_localizer["DispatchNotOnHold"].Value);
            if (string.IsNullOrWhiteSpace(newMachineCode)) return JsonFail(_localizer["MachineCodeRequired"].Value);

            var target = newMachineCode.Trim();
            if (target == dispatch.MachineCode) return JsonFail(_localizer["DispatchSwitchSameMachine"].Value);

            var targetStatus = await Db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.MachineCode == target && m.WorkCenter!.PlantId == dispatch.PlantId)
                .Select(m => (MachineStatus?)m.MachineStatus)
                .FirstOrDefaultAsync();
            if (targetStatus == null) return JsonFail(_localizer["MachineNotFound"].Value);
            if (DispatchHold.IsMachineDown(targetStatus.Value))
                return JsonFail(_localizer["DispatchSwitchMachineDown"].Value);

            var lifecycle = HttpContext.RequestServices.GetRequiredService<IDispatchLifecycleService>();
            var continuation = await lifecycle.SwitchMachineAsync(dispatch, target, DateTime.UtcNow);
            return await SaveOrConflictAsync(_localizer["DispatchSwitched", continuation.DispatchId, target].Value);
        }

        /// <summary>
        /// 剩餘量結案：暫停的派工不再生產剩餘量。有產出→以已完成量完工；沒有產出→取消。
        /// 工單其餘派工都已結束時，工單隨之自動完工。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ShortClose(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return JsonFail(_localizer["NoUpdatePermission"].Value);

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null) return JsonFail(_localizer["RecordNotFound"].Value);
            if (dispatch.Status != DispatchStatus.OnHold) return JsonFail(_localizer["DispatchNotOnHold"].Value);

            var lifecycle = HttpContext.RequestServices.GetRequiredService<IDispatchLifecycleService>();
            await lifecycle.ShortCloseAsync(dispatch, DateTime.UtcNow);
            return await SaveOrConflictAsync(_localizer["DispatchShortClosed"].Value);
        }

        /// <summary>存檔；派工被別人同時異動（xmin）時回 409。</summary>
        private async Task<IActionResult> SaveOrConflictAsync(string successMessage)
        {
            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                ConcurrencyConflictLog.Desktop(HttpContext, "Save", null);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return JsonFail(_localizer["ConcurrencyConflict"].Value);
            }
            return JsonOk(message: successMessage);
        }

        /// <summary>
        /// 派工拆分（Pending/Scheduled 可拆，§10-7）。
        /// splits: [{machineCode, qty}, ...] — 至少 2 筆，數量加總必須等於原派工量。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Split(string id,
            [FromBody] List<SplitItemDto> splits)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            if (dispatch.Status != DispatchStatus.Pending && dispatch.Status != DispatchStatus.Scheduled)
                return Json(new { success = false, message = _localizer["DispatchCannotAdjustStatus"].Value });

            if (splits == null || splits.Count < 2)
                return Json(new { success = false, message = _localizer["SplitMinTwo"].Value });

            var totalSplit = splits.Sum(s => s.Qty);
            if (Math.Abs(totalSplit - dispatch.Quantity) > 0.000001m)
                return Json(new { success = false, message = _localizer["SplitQtySumMismatch"].Value });

            if (splits.Any(s => s.Qty <= 0))
                return Json(new { success = false, message = _localizer["SplitQtyPositive"].Value });

            var now = DateTime.UtcNow;
            var groupId = dispatch.SplitGroupId ?? dispatch.DispatchId;

            // 原派工：更新為第一份
            dispatch.Quantity        = splits[0].Qty;
            dispatch.MachineCode     = string.IsNullOrWhiteSpace(splits[0].MachineCode) ? dispatch.MachineCode : splits[0].MachineCode.Trim();
            // 拆分時指定了機台＝人指定，重排不得改掉；沒指定的維持原本的鎖定狀態
            if (!string.IsNullOrWhiteSpace(splits[0].MachineCode)) dispatch.IsMachineLocked = true;
            dispatch.SplitGroupId    = groupId;
            dispatch.ScheduledStart  = null;
            dispatch.ScheduledEnd    = null;
            dispatch.MaterialRisk    = false;
            dispatch.Status          = DispatchStatus.Pending;
            dispatch.UpdatedAt       = now;

            // 子派工序號續編：拆分後原派工仍為 Pending、可再次拆分，固定從 -s1 起編會撞先前子派工主鍵。
            // 取本派工既有子派工的最大序號，往後續編，確保重複拆分不衝突。
            var childPrefix = $"{dispatch.DispatchId}-s";
            var existingChildIds = await Db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId.StartsWith(childPrefix))
                .Select(d => d.DispatchId)
                .ToListAsync();
            int nextChildSeq = 1;
            foreach (var cid in existingChildIds)
            {
                var tail = cid.Substring(childPrefix.Length);
                if (int.TryParse(tail, out var n) && n >= nextChildSeq)
                    nextChildSeq = n + 1;
            }

            // 新增子派工（第 2 份起）
            for (int i = 1; i < splits.Count; i++)
            {
                var child = new Dispatch
                {
                    DispatchId     = $"{childPrefix}{nextChildSeq++}",
                    WorkOrderId    = dispatch.WorkOrderId,
                    OperationSeq   = dispatch.OperationSeq,
                    PlantId        = dispatch.PlantId,
                    PrevDispatchId = dispatch.PrevDispatchId,
                    SplitGroupId   = groupId,
                    Quantity       = splits[i].Qty,
                    MachineCode    = string.IsNullOrWhiteSpace(splits[i].MachineCode) ? null : splits[i].MachineCode.Trim(),
                    IsMachineLocked = !string.IsNullOrWhiteSpace(splits[i].MachineCode),
                    ToolingCode    = dispatch.ToolingCode,
                    Status         = DispatchStatus.Pending,
                    IsOutsource    = dispatch.IsOutsource,
                    CreatedAt      = now,
                    UpdatedAt      = now,
                    CreatedByRoleId         = dispatch.CreatedByRoleId,
                    OrganizationUnitPath    = dispatch.OrganizationUnitPath
                };
                Db.Dispatches.Add(child);
            }

            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["SplitSuccess", splits.Count].Value });
        }

        /// <summary>轉委外（Pending/Scheduled 可轉，釋放機台時段，IsOutsource → true）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ToOutsource(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var dispatch = await Db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            if (dispatch.Status != DispatchStatus.Pending && dispatch.Status != DispatchStatus.Scheduled)
                return Json(new { success = false, message = _localizer["DispatchCannotAdjustStatus"].Value });

            dispatch.IsOutsource     = true;
            dispatch.MachineCode     = null;
            dispatch.ScheduledStart  = null;
            dispatch.ScheduledEnd    = null;
            dispatch.MaterialRisk    = false;
            dispatch.Status          = DispatchStatus.Pending;
            dispatch.UpdatedAt       = DateTime.UtcNow;
            await Db.SaveChangesAsync();

            return Json(new { success = true, message = _localizer["ToOutsourceSuccess"].Value });
        }

        /// <summary>
        /// §10 / §21 提早投產：釋放本工單的 WorkOrderMaterial 預留，
        /// 讓同物料的後續需求重新計算可用量（mfg-domain.md 業務規則 5）。
        /// </summary>
        private async Task RecalcReservationsOnEarlyStartAsync(string workOrderId, string? plantId)
        {
            if (string.IsNullOrEmpty(plantId)) return;
            await _reservations.ReleaseBySourceAsync(
                ReservationSourceType.WorkOrderMaterial, workOrderId);
        }

        // GET: /Dispatch/Print/{id}（2026-10-27：派工單紙本，隨料到現場）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.Dispatches.AsNoTracking()
                .Include(d => d.Operation!).ThenInclude(o => o.WorkOrder)
                .Include(d => d.Materials)
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            var op = doc.Operation;
            var productCode = op?.WorkOrder?.ProductCode;
            var n = await PrintNames.ForAsync(doc, new { ProductCode = productCode, WorkCenterId = op?.WorkCenterId });
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var p = new PrintDocument
            {
                Title = L["Print_Title_Dispatch"], DocNo = doc.DispatchId,
                DateLabel = L["Print_ScheduledStart"], DateValue = PrintTime(doc.ScheduledStart),
            }
                .Field(L["Print_WorkOrder"], doc.WorkOrderId)
                .Field(L["Print_Operation"], $"{doc.OperationSeq} {op?.OperationName}".Trim())
                .Field(L["Print_Product"], string.IsNullOrEmpty(productCode) ? null : $"{productCode} {n.Product(productCode)}".Trim())
                .Field(L["Print_WorkCenter"], n.WorkCenterWith(op?.WorkCenterId))
                .Field(L["Print_Machine"], n.MachineWith(doc.MachineCode))
                .Field(L["Print_Tooling"], doc.ToolingCode)
                .Field(L["Print_DispatchQty"], f.Qty(doc.Quantity, null))
                .Field(L["Print_QtyDone"], f.Qty(doc.QtyDone, null))
                .Field(L["Print_Outsource"], doc.IsOutsource ? L["Print_Yes"] : null)
                .Field(L["Print_ScheduledEnd"], PrintTime(doc.ScheduledEnd))
                .Field(L["Print_Status"], MfgEnumDisplay.Text(L, doc.Status));
            if (doc.Materials.Count > 0)
            {
                var tbl = new PrintTable { Title = L["Print_Materials"] }
                    .Col(L["Print_Seq"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                    .Col(L["Print_RequiredQty"], true).Col(L["Print_IssuedQty"], true).Col(L["Print_Uom"]);
                foreach (var m in doc.Materials.OrderBy(x => x.BomSeq))
                    tbl.Row(m.BomSeq.ToString(), m.ProductCode, n.Product(m.ProductCode),
                        f.Qty(m.RequiredQty, m.UomCode), f.Qty(m.IssuedQty, m.UomCode), n.UomWith(m.UomCode));
                p.Tables.Add(tbl);
            }
            return PrintDocumentView(p);
        }
    }

    /// <summary>派工拆分項目 DTO（POST /Dispatch/Split body）。</summary>
    public class SplitItemDto
    {
        public string? MachineCode { get; set; }
        public decimal Qty { get; set; }
    }

    /// <summary>換機續做可選的機台（暫停處理側板）。</summary>
    public record HoldSwitchMachineOption(string MachineCode, string? MachineName, string WorkCenterCode);

    /// <summary>派工清單 ViewModel（含 WorkOrderOperation / WorkOrder 聯結欄位）。</summary>
    public class DispatchListVm
    {
        public string DispatchId     { get; set; } = string.Empty;
        public string WorkOrderId    { get; set; } = string.Empty;
        public string ProductCode    { get; set; } = string.Empty;
        public string? UomCode       { get; set; }
        public int    OperationSeq   { get; set; }
        public string? OperationName { get; set; }
        public string  WorkCenterId  { get; set; } = string.Empty;
        public string? MachineCode   { get; set; }
        public decimal Quantity      { get; set; }
        public decimal QtyDone       { get; set; }
        public DispatchStatus Status { get; set; }
        public bool IsOutsource      { get; set; }
        public DateTime? ScheduledStart { get; set; }
        public DateTime? PlannedEndDate { get; set; }
        public string? ToolingCode   { get; set; }
        /// <summary>模具累計打次已達保養門檻（§14.1），清單標示「模具需保養」。</summary>
        public bool ToolingPmDue     { get; set; }
        /// <summary>暫停原因（僅暫停中有值），清單於狀態旁標示。</summary>
        public DispatchHoldReason? HoldReason { get; set; }
    }
}
