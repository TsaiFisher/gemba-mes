using SmartFactory.MES.Modules._Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using System.Text.Json;

namespace SmartFactory.MES.Api.Mobile
{
    // ── 共用回應格式（mobile-pwa.md §8.2）─────────────────────────────────────

    public record MobileOkResult(bool Success = true, object? Data = null, string? Message = null);
    public record MobileErrorResult(bool Success = false, MobileError? Error = null);
    public record MobileError(string Code, string Message, string? Field = null);

    // ── 請求 Body ────────────────────────────────────────────────────────────

    public record SetupStartRequest(string? OperatorId, string ClientId, DateTime ClientTimestamp, string? ToolingCode);
    public record SetupDoneRequest(string ClientId, DateTime ClientTimestamp);
    public record SessionStartRequest(string DispatchId, string? MachineId, string? MoldId, string? OperatorId, string ClientId, DateTime ClientTimestamp);
    public record SessionStopRequest(string ClientId, DateTime ClientTimestamp);
    public record ReportCreateRequest(string SessionId, decimal QtyOk, decimal QtyNg, string? ContainerId, string ClientId, DateTime ClientTimestamp, string? NgReasonCode = null);
    /// <summary>完工＝結束本次上工，可一併回報最後一批數量（皆為 0 時等同純下工）。</summary>
    public record SessionCompleteRequest(string ClientId, DateTime ClientTimestamp, decimal QtyOk = 0, decimal QtyNg = 0, string? ContainerId = null, string? NgReasonCode = null);
    public record MachineStatusEventRequest(string EventType, string? Note, string? SourceDocId, string ClientId, DateTime ClientTimestamp);

    public record InspectionSubmitRequest(
        string DispatchId,
        SmartFactory.MES.Models.InspectionOrderType OrderType,
        int FailQty,
        int ActualSampleQty,
        string ClientId,
        DateTime ClientTimestamp,
        IEnumerable<InspectionItemInput>? Items = null);

    public record InspectionItemInput(
        int SeqNo,
        string? ItemName,
        string? UomCode,
        decimal? LowerSpec,
        decimal? TargetValue,
        decimal? UpperSpec,
        decimal? MeasuredValue,
        bool? IsConforming,      // 不採用：伺服器依量測值與規格重算（MobileInspectionJudge）
        decimal? TargetStdDev,   // 不採用：取自規格
        string? SpecItemId = null);

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 手機版派工 / 上工 / 報工 / 機台狀態 API（mfg-domain.md §8 / mobile-pwa.md §8.1）。
    /// 所有寫入 API 支援 clientId 去重（mobile-pwa.md §8.3 / §12.2）。
    /// 認證：Cookie（與桌機共用）。
    /// </summary>
    [Route("api/mobile")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileMfgController : ControllerBase
    {
        private const string ModuleKey = "Mfg";

        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;
        private readonly IReportEtaNotifier _etaNotifier;
        private readonly ISamplingPlanService _samplingPlanService;
        private readonly IMobilePermissionService _mobilePermission;
        private readonly IProductionCostAccrualService _costAccrual;
        private readonly IStagingSourceService _stagingSource;
        private readonly IFpiGateService _fpiGate;
        private readonly IMfgSimplifiedModeService _simplified;
        private readonly IOperationSequenceGuard _sequenceGuard;

        public MobileMfgController(
            ApplicationDbContext db,
            IUserContext userContext,
            IReportEtaNotifier etaNotifier,
            ISamplingPlanService samplingPlanService,
            IMobilePermissionService mobilePermission,
            IProductionCostAccrualService costAccrual,
            IStagingSourceService stagingSource,
            IFpiGateService fpiGate,
            IMfgSimplifiedModeService simplifiedMode,
            IOperationSequenceGuard sequenceGuard)
        {
            _db   = db;
            _userContext = userContext;
            _etaNotifier = etaNotifier;
            _samplingPlanService = samplingPlanService;
            _mobilePermission = mobilePermission;
            _costAccrual = costAccrual;
            _stagingSource = stagingSource;
            _fpiGate = fpiGate;
            _simplified = simplifiedMode;
            _sequenceGuard = sequenceGuard;
        }

        /// <summary>
        /// 行動權限二次驗證（mobile-pwa.md §6）：角色未授權該作業時回 403。
        /// 回傳 null 表示通過。
        /// </summary>
        private async Task<IActionResult?> RequireMobilePermissionAsync(string actionKey, string moduleKey = ModuleKey)
        {
            var roleId = _userContext.ActiveRoleId;
            if (!string.IsNullOrEmpty(roleId)
                && await _mobilePermission.CanExecuteAsync(roleId, moduleKey, actionKey))
                return null;

            return StatusCode(StatusCodes.Status403Forbidden,
                new MobileErrorResult(Error: new("FORBIDDEN", PzText.Get("Mob_ForbiddenAction"))));
        }

        /// <summary>
        /// 上工／備料的作業員一律是登入者本人；帶了別人的代號回 403。
        ///
        /// <para>原本是 <c>req.OperatorId ?? 目前登入者</c>：手機前端從來不送這個欄位，但伺服器照收、
        /// 也不檢查，改過的前端（或直接打 API）就能用別人的名義上工，工時與績效記到那個人頭上。
        /// 桌機的上工／備料也只認 <c>UserContext.UserId</c>。欄位保留是為了舊版 App 送了本人代號時不會被擋。</para>
        /// </summary>
        private IActionResult? RejectForeignOperator(string? requestedOperatorId) =>
            string.IsNullOrWhiteSpace(requestedOperatorId)
            || string.Equals(requestedOperatorId, _userContext.UserId, StringComparison.Ordinal)
                ? null
                : StatusCode(StatusCodes.Status403Forbidden,
                    new MobileErrorResult(Error: new("OPERATOR_MISMATCH", PzText.Get("Mob_OperatorMismatch"))));

        /// <summary>
        /// 上工時現場改填的機台，必須是本廠、且屬於該派工工序所屬工作中心的機台；
        /// 模具只能是排程指定的那一副。沒填或與派工相同則照舊。
        ///
        /// <para>原本 <c>req.MachineId ?? dispatch.MachineCode</c>／<c>req.MoldId</c> 照單全收：
        /// 打錯字、別廠的機台、甚至不存在的代號都會寫進上工紀錄，機台稼動與模具打次就跟著記到錯的地方。
        /// <c>WorkCenterMachine</c> 不是 <c>IPlantEntity</c>，廠別要經由 WorkCenter 帶條件
        /// （與 <see cref="MachineStatusEvent"/> 同一個理由）。</para>
        /// </summary>
        private async Task<IActionResult?> RejectForeignMachineAsync(Dispatch dispatch, string? machineId, string? moldId)
        {
            if (!string.IsNullOrWhiteSpace(moldId) && moldId != dispatch.ToolingCode)
                return BadRequest(new MobileErrorResult(Error: new("TOOLING_MISMATCH", PzText.Get("Mob_ToolingMismatch"))));

            if (string.IsNullOrWhiteSpace(machineId) || machineId == dispatch.MachineCode) return null;

            var workCenterId = await _db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq)
                .Select(o => o.WorkCenterId)
                .FirstOrDefaultAsync();
            var allowed = workCenterId != null && await _db.WorkCenterMachines.AsNoTracking()
                .AnyAsync(m => m.MachineCode == machineId && m.WorkCenterCode == workCenterId
                            && m.WorkCenter!.PlantId == dispatch.PlantId);

            return allowed ? null
                : BadRequest(new MobileErrorResult(Error: new("MACHINE_NOT_ALLOWED", PzText.Get("Mob_MachineNotInWorkCenter"))));
        }

        /// <summary>機台狀態的中文名稱，用語與桌機 ProjectZSharedResource 的 MachineStatusXXX 一致。</summary>
        private static string MachineStatusLabel(MachineStatus? status) => status switch
        {
            MachineStatus.Available      => PzText.Get("Mob_MachineStatus_Available"),
            MachineStatus.Standby        => PzText.Get("Mob_MachineStatus_Standby"),
            MachineStatus.ExternalRepair => PzText.Get("Mob_MachineStatus_ExternalRepair"),
            MachineStatus.BreakDown      => PzText.Get("Mob_MachineStatus_BreakDown"),
            MachineStatus.Modification   => PzText.Get("Mob_MachineStatus_Modification"),
            MachineStatus.Loaned         => PzText.Get("Mob_MachineStatus_Loaned"),
            MachineStatus.Retired        => PzText.Get("Mob_MachineStatus_Retired"),
            MachineStatus.Maintenance    => PzText.Get("Mob_MachineStatus_Maintenance"),
            _                            => status?.ToString() ?? "",
        };

        /// <summary>
        /// 模組層閘門：角色在此手機模組**任一**作業被授權即放行（＝首頁看得到這張模組磚）。
        ///
        /// 給「多個作業頁共用的查詢端點」用。例如 GET dispatches 被上工、報工、FPI、IPQC
        /// 四個頁面共用，鎖任一單一 actionKey 都會讓其他三頁的清單變空白，而前端只是
        /// 顯示「沒有派工」不會報錯——靜默壞掉。放行條件與首頁磚一致，故不會多開任何人。
        /// </summary>
        private async Task<IActionResult?> RequireMobileModuleAsync(string moduleKey = ModuleKey)
        {
            var roleId = _userContext.ActiveRoleId;
            if (!string.IsNullOrEmpty(roleId)
                && (await _mobilePermission.GetAllowedActionKeysAsync(roleId, moduleKey)).Any())
                return null;

            return StatusCode(StatusCodes.Status403Forbidden,
                new MobileErrorResult(Error: new("FORBIDDEN", PzText.Get("Mob_ForbiddenModule"))));
        }

        // ── 派工列表 ──────────────────────────────────────────────────────────

        /// <summary>取得此機台的派工清單（已排定 / 生產中），按排程開始排序（mfg-domain.md §8.1）。</summary>
        [HttpGet("dispatches")]
        public async Task<IActionResult> GetDispatches(
            [FromQuery] string? machineId,
            [FromQuery] string? status = null)
        {
            // 上工／報工／FPI／IPQC 四頁共用，故走模組層閘門而非單一作業鍵。
            if (await RequireMobileModuleAsync() is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId;
            // 派工前自動重排（廠別參數開啟且排程已過時才會跑）
            await HttpContext.EnsureScheduleFreshAsync(plantId);
            var q = _db.Dispatches.AsNoTracking()
                .Where(d => d.PlantId == plantId);

            if (!string.IsNullOrEmpty(machineId))
                q = q.Where(d => d.MachineCode == machineId);

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<DispatchStatus>(status, out var st))
                q = q.Where(d => d.Status == st);
            else
            {
                // §25.4：簡化模式下派工停在 Pending（免排程），不納入的話即使開工放行了，
                // 現場在待辦清單上也看不到這張派工。狀態清單與開工放行同一份判定來源。
                var actionable = await _simplified.ActionableDispatchStatusesAsync(_userContext.PlantId);
                q = q.Where(d => actionable.Contains(d.Status));
            }

            // 目前登入者在這張派工上「還開著」的上工紀錄。上工頁原本只靠 sessionStorage 記 sessionId：
            // 離線「完工並報工」排進佇列、同步後伺服器已下工，手機上那份卻還在，畫面仍顯示「完工」鈕，
            // 一按就是「此上工記錄已下工」（2026-10-05 實測）；反過來，別台手機開的工這裡也接不回來。
            // 以伺服器為準，頁面拿這兩欄對帳。
            var operatorId = _userContext.UserId;
            var list = await q
                .OrderBy(d => d.ScheduledStart)
                .Select(d => new
                {
                    d.DispatchId,
                    d.WorkOrderId,
                    d.OperationSeq,
                    d.MachineCode,
                    d.Quantity,
                    d.QtyDone,
                    Status = d.Status.ToString(),
                    d.IsOutsource,
                    ScheduledStart = d.ScheduledStart,
                    ScheduledEnd   = d.ScheduledEnd,
                    MyOpenSession = _db.ProductionSessions
                        .Where(s => s.DispatchId == d.DispatchId
                                    && s.Status == ProductionSessionStatus.Open
                                    && s.OperatorId == operatorId)
                        .OrderByDescending(s => s.StartAt)
                        .Select(s => new { s.SessionId, s.StartAt })
                        .FirstOrDefault()
                })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: list));
        }

        // ── Setup 備料 ───────────────────────────────────────────────────────

        /// <summary>備料狀態：本派工已開的備料單 + 尚未備妥的用料（現場備料畫面用）。</summary>
        [HttpGet("dispatches/{id}/staging")]
        public async Task<IActionResult> GetStaging(string id)
        {
            if (await RequireMobilePermissionAsync("Setup") is { } forbidden) return forbidden;

            var dispatch = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            var plantId = dispatch.PlantId ?? "";
            var orders  = await _stagingSource.GetExistingOrdersAsync("Dispatch", id, plantId);
            var pending = await _stagingSource.GetLinesAsync("Dispatch", id, plantId);

            return Ok(new MobileOkResult(Data: new
            {
                Orders = orders.Select(o => new
                {
                    o.StagingId,
                    o.Status,
                    o.StagingWhCode,
                    o.StagingLocationCode,
                    o.LineCount,
                }),
                Pending = pending.Select(p => new
                {
                    p.ProductCode,
                    p.ProductName,
                    p.RequiredQty,
                    p.UomCode,
                }),
            }));
        }

        /// <summary>由本派工建立草稿備料單（帶入未發料用料＋預設備料倉）。clientId 去重。</summary>
        [HttpPost("dispatches/{id}/staging-order")]
        public async Task<IActionResult> CreateStagingOrder(string id, [FromBody] SetupDoneRequest req)
        {
            if (await RequireMobilePermissionAsync("Setup") is { } forbidden) return forbidden;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var dispatch = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            string stagingId;
            try
            {
                stagingId = await _stagingSource.CreateDraftAsync(
                    "Dispatch", id, dispatch.PlantId ?? "", _userContext.UserId ?? "");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new MobileErrorResult(Error: new("NO_PENDING_MATERIAL", ex.Message)));
            }

            var result = new MobileOkResult(Data: new { StagingId = stagingId }, Message: PzText.Get("Mob_StagingCreated"));
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp,
                $"/api/mobile/dispatches/{id}/staging-order", result);
        }

        /// <summary>
        /// 開始備料（mfg-domain.md §8.2）。clientId 去重。
        /// 模具沿用排程指定的 <c>Dispatch.ToolingCode</c>，請求中的 ToolingCode 已不再採用
        /// （欄位保留供舊版 App 相容，不影響行為）。
        /// </summary>
        [HttpPost("dispatches/{id}/setup/start")]
        public async Task<IActionResult> SetupStart(string id, [FromBody] SetupStartRequest req)
        {
            if (await RequireMobilePermissionAsync("Setup") is { } forbidden) return forbidden;
            if (RejectForeignOperator(req.OperatorId) is { } foreign) return foreign;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);
            var setupOperatorId  = _userContext.UserId;
            // 模具不再由現場回報，一律沿用排程指定的 Dispatch.ToolingCode
            var setupToolingCode = dispatch.ToolingCode;

            var validEntryStatus = dispatch.Status is DispatchStatus.Scheduled or DispatchStatus.ReadyToStart
                || (isProductionLine && dispatch.Status == DispatchStatus.SetupInProgress);
            if (!validEntryStatus)
                return BadRequest(new MobileErrorResult(Error: new("INVALID_STATUS", PzText.Get("Mob_DispatchCannotSetup"))));

            var existing = isProductionLine
                ? await _db.ProductionSetups.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSetupStatus.Open && s.OperatorId == setupOperatorId)
                : await _db.ProductionSetups.AnyAsync(s => s.DispatchId == id
                    && s.Status == ProductionSetupStatus.Open);
            if (existing)
                return Conflict(new MobileErrorResult(Error: new("SETUP_ALREADY_OPEN", PzText.Get("Mob_SetupAlreadyOpen"))));

            var now = DateTime.UtcNow;
            var setup = new ProductionSetup
            {
                SetupId      = Guid.NewGuid().ToString("N"),
                DispatchId   = dispatch.DispatchId,
                WorkOrderId  = dispatch.WorkOrderId,
                Status       = ProductionSetupStatus.Open,
                SetupStartAt = now,
                OperatorId   = setupOperatorId,
                ToolingCode  = setupToolingCode,
                PlantId      = dispatch.PlantId,
                CreatedAt    = now,
                UpdatedAt    = now
            };
            _db.ProductionSetups.Add(setup);

            if (dispatch.Status != DispatchStatus.SetupInProgress)
            {
                dispatch.Status    = DispatchStatus.SetupInProgress;
                dispatch.UpdatedAt = now;
            }

            var result = new MobileOkResult(Data: new { setup.SetupId }, Message: PzText.Get("Mob_SetupStarted"));
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/dispatches/setup/start", result);
        }

        /// <summary>備料完成（mfg-domain.md §8.2）。clientId 去重。</summary>
        [HttpPatch("dispatches/{id}/setup/done")]
        public async Task<IActionResult> SetupDone(string id, [FromBody] SetupDoneRequest req)
        {
            if (await RequireMobilePermissionAsync("Setup") is { } forbidden) return forbidden;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            if (dispatch.Status != DispatchStatus.SetupInProgress)
                return BadRequest(new MobileErrorResult(Error: new("INVALID_STATUS", PzText.Get("Mob_DispatchNotInSetup"))));

            var openSetups = await _db.ProductionSetups
                .Where(s => s.DispatchId == id && s.Status == ProductionSetupStatus.Open)
                .ToListAsync();
            if (openSetups.Count == 0)
                return NotFound(new MobileErrorResult(Error: new("SETUP_NOT_FOUND", PzText.Get("Mob_SetupNotFound"))));

            var now = DateTime.UtcNow;
            foreach (var s in openSetups)
            {
                s.SetupEndAt = now;
                s.Status     = ProductionSetupStatus.Closed;
                s.UpdatedAt  = now;
            }

            // 累計 Setup 成本（與桌機 DoneSetup 共用 ProductionCostAccrualService，避免雙軌漂移）：
            // 人工逐人計（備料人員可能非生產人員），製造費用依時間窗只算一次不隨人數疊加。
            await _costAccrual.AccrueOnSetupDoneAsync(openSetups, dispatch);

            // §26 FPI 閘口：工序要求首件**且尚未通過**才進 PendingFPI，與桌機 DoneSetup 共用同一份判定。
            // 曾只看 RequiresFpi：已通過首件的派工再備料一次就被打回等待首件，開工又不放行
            // PendingFPI，品管只好重做一次首件。
            var fpi = await _fpiGate.EvaluateAsync(dispatch);
            dispatch.Status    = fpi.IsBlocked ? DispatchStatus.PendingFPI : DispatchStatus.ReadyToStart;
            dispatch.UpdatedAt = now;

            var setupMsg = dispatch.Status == DispatchStatus.PendingFPI ? PzText.Get("Mob_SetupDoneAwaitFpi") : PzText.Get("Mob_SetupDone");
            var result   = new MobileOkResult(Message: setupMsg,
                Data: new { RequiresFpi = dispatch.Status == DispatchStatus.PendingFPI });
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/dispatches/setup/done", result);
        }

        // ── 開工 / 下工 ──────────────────────────────────────────────────────

        /// <summary>開工（建立 ProductionSession）（mfg-domain.md §8.3）。clientId 去重。</summary>
        [HttpPost("sessions")]
        public async Task<IActionResult> StartSession([FromBody] SessionStartRequest req)
        {
            if (await RequireMobilePermissionAsync("StartWork") is { } forbidden) return forbidden;
            return await WithConcurrencyRetryAsync(() => StartSessionOnceAsync(req));
        }

        private async Task<IActionResult> StartSessionOnceAsync(SessionStartRequest req)
        {            if (RejectForeignOperator(req.OperatorId) is { } foreign) return foreign;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == req.DispatchId);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            if (await RejectForeignMachineAsync(dispatch, req.MachineId, req.MoldId) is { } badMachine) return badMachine;

            // §26 首件檢查閘口：以「工序要求 FPI 且尚無 Pass 檢驗單」為準，不以派工狀態為準——
            // 舊版只擋 PendingFPI，而該狀態只能經備料進入，不做備料直接開工即可繞過。
            // 僅在首次開工時檢查，續工（已 InProgress）不重複阻擋。
            if (dispatch.Status != DispatchStatus.InProgress
                && (await _fpiGate.EvaluateAsync(dispatch)).IsBlocked)
                return BadRequest(new MobileErrorResult(Error: new("PENDING_FPI", PzText.Get("Mob_AwaitingFpi"))));

            // §25.4 簡化模式額外放行 Pending；放行清單與桌機共用 IMfgSimplifiedModeService。
            if (!await _simplified.CanStartSessionAsync(dispatch.Status, _userContext.PlantId))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_STATUS", PzText.Get("Mob_DispatchCannotStart"))));

            // §18.2 投產前審核：與桌機共用 WorkOrderApprovalGate
            switch (await WorkOrderApprovalGate.CheckStartAsync(_db, dispatch.WorkOrderId))
            {
                case WorkOrderStartBlock.NotReleased:
                    return BadRequest(new MobileErrorResult(Error: new("WO_NOT_RELEASED", PzText.Get("Mob_WorkOrderNotReleased"))));
                case WorkOrderStartBlock.NotApproved:
                    return BadRequest(new MobileErrorResult(Error: new("WO_NOT_APPROVED", PzText.Get("Mob_WorkOrderNotApproved"))));
            }

            // 機台狀態閘口（廠別參數 MfgMachineStatusGate，預設 Warning）：與桌機、排程共用 MachineAvailability。
            // 原本上工完全不看機台狀態，「可開工」的派工照樣能在故障停機的機台上開工。
            var machineCheck = await MachineAvailability.CheckStartAsync(
                _db, HttpContext.RequestServices.GetRequiredService<IPlantParamService>(),
                dispatch.PlantId, req.MachineId ?? dispatch.MachineCode);
            if (machineCheck.Blocked)
                return BadRequest(new MobileErrorResult(Error: new("MACHINE_UNAVAILABLE",
                    PzText.Get("Mob_MachineUnavailableFmt", MachineStatusLabel(machineCheck.Status)))));

            // 生產線（WorkCenter.IsProductionLine）允許多人同時上工；一般機台維持同時只能 1 人在線上。
            var isProductionLine = await IsProductionLineDispatchAsync(dispatch);
            var operatorId = _userContext.UserId;
            var existing = isProductionLine
                ? await _db.ProductionSessions.AnyAsync(s => s.DispatchId == req.DispatchId
                    && s.Status == ProductionSessionStatus.Open && s.OperatorId == operatorId)
                : await _db.ProductionSessions.AnyAsync(s => s.DispatchId == req.DispatchId
                    && s.Status == ProductionSessionStatus.Open);
            if (existing)
                return Conflict(new MobileErrorResult(Error: new("SESSION_ALREADY_OPEN", PzText.Get("Mob_SessionAlreadyOpen"))));

            var now = DateTime.UtcNow;
            var session = new ProductionSession
            {
                SessionId   = Guid.NewGuid().ToString("N"),
                DispatchId  = dispatch.DispatchId,
                WorkOrderId = dispatch.WorkOrderId,
                Status      = ProductionSessionStatus.Open,
                StartAt     = now,
                OperatorId  = operatorId,
                MachineCode = req.MachineId ?? dispatch.MachineCode,
                ToolingCode = req.MoldId,
                PlantId     = dispatch.PlantId,
                CreatedAt   = now,
                UpdatedAt   = now
            };
            _db.ProductionSessions.Add(session);

            // 改選的機台已由 RejectForeignMachineAsync 驗證為同工作中心的本廠機台
            await MachineAvailability.SyncDispatchToActualMachineAsync(_db, dispatch, req.MachineId, now);

            bool isFirstStart = dispatch.Status != DispatchStatus.InProgress;
            if (isFirstStart)
            {
                dispatch.Status    = DispatchStatus.InProgress;
                dispatch.UpdatedAt = now;

                var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
                if (wo is { Status: WorkOrderStatus.Released })
                {
                    wo.Status    = WorkOrderStatus.InProgress;
                    wo.UpdatedAt = now;
                }
            }

            // IPQC 觸發：工序標記 RequiresInspection=true 時，首次開工自動建立 Pending 待檢單（§15.3 / Phase H）
            if (isFirstStart && dispatch.PlantId != null)
            {
                var op = await _db.WorkOrderOperations.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId
                                           && o.OperationSeq == dispatch.OperationSeq);
                if (op?.RequiresInspection == true)
                {
                    var lotSize = (int)dispatch.Quantity;
                    var wo = await _db.WorkOrders.AsNoTracking()
                        .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);

                    // 調用 MIL-STD-1916 計算抽樣數與允收數
                    var decision = await _samplingPlanService.GetDecisionAsync(
                        productCode: wo?.ProductCode ?? "",
                        supplierCode: null,
                        inspectionType: "IPQC",
                        lotSize: lotSize,
                        plantId: dispatch.PlantId);

                    _db.InspectionOrders.Add(new SmartFactory.MES.Models.InspectionOrder
                    {
                        InspectionType    = "IPQC",
                        PlantId           = dispatch.PlantId,
                        SourceDocId       = dispatch.DispatchId,
                        ProductCode       = wo?.ProductCode ?? "",
                        LotSize           = lotSize,
                        SampleSize        = decision?.SampleSize ?? 0,
                        VerificationLevel = decision?.VerificationLevel ?? 4,
                        Status            = "Pending",
                        CreatedAt         = now,
                        UpdatedAt         = now
                    });
                }
            }

            // Warning：照常上工，機台狀態放在 data 讓手機依語系顯示警示（i18n 的 machine.status.*）。
            var result = new MobileOkResult(
                Data: new { session.SessionId, MachineWarning = machineCheck.Warned ? machineCheck.Status?.ToString() : null },
                Message: machineCheck.Warned
                    ? PzText.Get("Mob_ClockInMachineWarnFmt", MachineStatusLabel(machineCheck.Status))
                    : PzText.Get("Mob_ClockInOk"));
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/sessions", result);
        }

        /// <summary>下工（關閉 ProductionSession + 累計成本）（mfg-domain.md §5.3）。clientId 去重。</summary>
        [HttpPatch("sessions/{sessionId}/stop")]
        public async Task<IActionResult> StopSession(string sessionId, [FromBody] SetupDoneRequest req)
        {
            if (await RequireMobilePermissionAsync("StopWork") is { } forbidden) return forbidden;
            return await WithConcurrencyRetryAsync(() => StopSessionOnceAsync(sessionId, req));
        }

        private async Task<IActionResult> StopSessionOnceAsync(string sessionId, SetupDoneRequest req)
        {
            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var session = await _db.ProductionSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (session == null)
                return NotFound(new MobileErrorResult(Error: new("SESSION_NOT_FOUND", PzText.Get("Mob_SessionNotFound"))));

            if (session.Status != ProductionSessionStatus.Open)
                return BadRequest(new MobileErrorResult(Error: new("SESSION_NOT_OPEN", PzText.Get("Mob_SessionAlreadyClosed"))));

            var now = DateTime.UtcNow;
            var (endAt, rejectTime) = await ResolveOccurredAtAsync(req.ClientTimestamp, session.PlantId, session.StartAt, now);
            if (rejectTime != null) return rejectTime;

            session.EndAt     = endAt;
            session.Status    = ProductionSessionStatus.Closed;
            session.UpdatedAt = now;

            // 累計實際人工/機台成本（mfg-domain.md §16.3；與 Web 端 DispatchController 共用同一份邏輯）
            await _costAccrual.AccrueOnSessionStopAsync(session);

            var result = new MobileOkResult(Message: PzText.Get("Mob_ClockOutOk"));
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/sessions/stop", result);
        }

        // ── IPQC / FPI 檢驗（Phase H）────────────────────────────────────────

        /// <summary>查詢派工的待執行檢驗單（FPI / IPQC）</summary>
        [HttpGet("dispatches/{id}/pending-inspection")]
        public async Task<IActionResult> GetPendingInspection(string id)
        {
            if (await RequireMobilePermissionAsync("Inspect") is { } forbidden) return forbidden;

            var dispatch = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == id);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            // 取最近一張 Pending 的檢驗單
            var order = await _db.InspectionOrders
                .AsNoTracking()
                .Where(o => o.SourceDocId == id && o.Status == "Pending")
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync();

            // 取規格項目（供前端顯示輸入欄位）
            var specItems = order == null ? null : await _db.InspectionSpecItems
                .AsNoTracking()
                .Where(si => _db.InspectionOrders
                    .Where(o => o.InspectionOrderId == order.InspectionOrderId)
                    .Select(o => o.SourceDocId)
                    .Contains(si.SpecId))
                .OrderBy(si => si.SeqNo)
                .Select(si => new
                {
                    si.SpecItemId, si.SeqNo, si.ItemName, si.UomCode,
                    si.LowerSpec, si.TargetValue, si.UpperSpec, si.TargetStdDev
                })
                .ToListAsync();

            // 若有 InspectionOrder 附上規格項目，讓前端可呈現量測清單
            if (order != null)
            {
                var spec = await _db.InspectionSpecs.AsNoTracking()
                    .Where(s => s.IsActive &&
                                _db.InspectionOrders
                                    .Where(o => o.InspectionOrderId == order.InspectionOrderId)
                                    .Select(o => o.ProductCode)
                                    .Contains(s.ProductCode ?? ""))
                    .OrderByDescending(s => s.ProductCode != null)
                    .FirstOrDefaultAsync();

                var items = spec == null ? new List<object>() :
                    (await _db.InspectionSpecItems.AsNoTracking()
                        .Where(si => si.SpecId == spec.SpecId)
                        .OrderBy(si => si.SeqNo)
                        .ToListAsync())
                    .Select(si => (object)new
                    {
                        si.SpecItemId, si.SeqNo, si.ItemName, si.UomCode,
                        si.LowerSpec, si.TargetValue, si.UpperSpec, si.TargetStdDev
                    }).ToList();

                return Ok(new MobileOkResult(Data: new
                {
                    InspectionOrderId = order.InspectionOrderId,
                    order.OrderType,
                    order.SampleSize,
                    order.VerificationLevel,
                    order.Status,
                    SpecItems = items
                }));
            }

            return Ok(new MobileOkResult(Data: (object?)null, Message: PzText.Get("Mob_NoPendingInspection")));
        }

        /// <summary>提交 FPI / IPQC 檢驗結果（mfg-domain.md §15.3 Phase H）。clientId 去重。</summary>
        [HttpPost("inspection-orders")]
        public async Task<IActionResult> SubmitInspection([FromBody] InspectionSubmitRequest req)
        {
            if (await RequireMobilePermissionAsync("Inspect") is { } forbidden) return forbidden;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            // 原本負數不良數判 Fail 還說得過去，但 0 抽樣也能讓首件「通過」、把派工解鎖。
            if (!MobileInspectionJudge.IsValidQty(req.ActualSampleQty, req.FailQty))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_QTY", PzText.Get("Mob_SampleQtyInvalid"))));

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == req.DispatchId);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            var now = DateTime.UtcNow;

            // 找對應的 Pending 檢驗單（可為系統自動建立或手動建立）
            var order = await _db.InspectionOrders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.SourceDocId == req.DispatchId
                                       && o.Status == "Pending"
                                       && o.OrderType == req.OrderType);

            if (order == null)
            {
                // 手動建立（品管臨時發起）
                var wo = await _db.WorkOrders.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
                var decision = await _samplingPlanService.GetDecisionAsync(
                    productCode: wo?.ProductCode ?? "",
                    supplierCode: null,
                    inspectionType: req.OrderType == SmartFactory.MES.Models.InspectionOrderType.FPI ? "FPI" : "IPQC",
                    lotSize: dispatch.Quantity,
                    plantId: dispatch.PlantId,
                    machineCode: dispatch.MachineCode);

                order = new SmartFactory.MES.Models.InspectionOrder
                {
                    InspectionType    = req.OrderType == SmartFactory.MES.Models.InspectionOrderType.FPI ? "FPI" : "IPQC",
                    OrderType         = req.OrderType,
                    PlantId           = dispatch.PlantId,
                    SourceDocId       = dispatch.DispatchId,
                    ProductCode       = wo?.ProductCode ?? "",
                    LotSize           = dispatch.Quantity,
                    SampleSize        = decision?.SampleSize ?? 0,
                    VerificationLevel = decision?.VerificationLevel ?? 4,
                    Status            = "Pending",
                    CreatedAt         = now,
                    UpdatedAt         = now
                };
                _db.InspectionOrders.Add(order);
            }

            // 填入量測明細。規格上下限、是否符合、3σ 警告與合格判定全由伺服器決定（MobileInspectionJudge）：
            // 手機送來的 IsConforming／TargetStdDev 與已對到規格的上下限一律不採用。
            // 判定依據：既有待檢單上的規格快照優先；手動建立的檢驗單沒有快照，改查規格主檔。
            var refs = order.Items.Count > 0
                ? order.Items.Select(i => new MobileInspectionJudge.SpecRef(null, i.SeqNo, i.ItemName, i.UomCode,
                    i.LowerSpec, i.TargetValue, i.UpperSpec, i.TargetStdDev)).ToList()
                : MobileInspectionJudge.FromSpec(await MobileInspectionJudge.FindSpecAsync(_db, dispatch.PlantId ?? "",
                    await _db.WorkOrders.AsNoTracking().Where(w => w.WorkOrderId == dispatch.WorkOrderId)
                        .Select(w => w.ProductCode).FirstOrDefaultAsync()));

            var judged = new List<SmartFactory.MES.Models.InspectionItem>();
            foreach (var item in req.Items ?? Enumerable.Empty<InspectionItemInput>())
            {
                var m = new MobileInspectionJudge.Measured(item.SpecItemId, item.SeqNo, item.ItemName, item.UomCode,
                    item.LowerSpec, item.TargetValue, item.UpperSpec, item.MeasuredValue);
                var spec  = MobileInspectionJudge.Match(m, refs);
                var built = MobileInspectionJudge.BuildItem(m, spec);

                var existingItem = spec == null ? null
                    : order.Items.FirstOrDefault(i => i.SeqNo == spec.SeqNo && i.ItemName == spec.ItemName);
                if (existingItem != null)
                {
                    existingItem.MeasuredValue   = built.MeasuredValue;
                    existingItem.IsConforming    = built.IsConforming;
                    existingItem.HasSigmaWarning = built.HasSigmaWarning;
                    judged.Add(existingItem);
                }
                else
                {
                    built.InspectionOrderId = order.InspectionOrderId;
                    _db.InspectionItems.Add(built);
                    judged.Add(built);
                }
            }

            // Pass / Fail 判定（Ac=0：不良數 > 0 或任一量測不符合即 Fail）
            var (passed, failQty)  = MobileInspectionJudge.Judge(req.FailQty, judged);
            order.FailQty          = failQty;
            order.ActualSampleQty  = req.ActualSampleQty;
            order.Status           = passed ? "Pass" : "Fail";
            order.InspectorId      = _userContext.UserId;
            order.InspectedAt      = now;
            order.UpdatedAt        = now;

            // 更新派工狀態（§26：Pass→ReadyToStart / Fail→OnHold 與桌機共用同一份實作）
            if (req.OrderType == SmartFactory.MES.Models.InspectionOrderType.FPI)
            {
                _fpiGate.ApplyResult(dispatch, passed, now);
            }
            else if (!passed && dispatch.Status == DispatchStatus.InProgress)
            {
                // IPQC Fail → 暫停（只暫停生產中的派工，與桌機 InspectionOrderController 一致）
                DispatchHold.Hold(dispatch, DispatchHoldReason.IpqcFailed, now);
            }

            // 更新 SamplingState（依 SamplingScope 計數）
            var dispatchWo = await _db.WorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            await _samplingPlanService.RecordResultAsync(
                productCode: dispatchWo?.ProductCode,
                supplierCode: null,
                plantId: dispatch.PlantId,
                passed: passed,
                machineCode: dispatch.MachineCode);

            var msg = passed
                ? req.OrderType == SmartFactory.MES.Models.InspectionOrderType.FPI
                    ? PzText.Get("Mob_FpiPassed")
                    : PzText.Get("Mob_InProcessInspectionPassed")
                : PzText.Get("Mob_InspectionFailedPaused");

            var result = new MobileOkResult(Message: msg,
                Data: new { order.InspectionOrderId, order.Status, DispatchStatus = dispatch.Status.ToString() });
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/inspection-orders", result);
        }

        // ── 報工 ─────────────────────────────────────────────────────────────

        /// <summary>報工（建立 ProductionReport + ETA 重算）（mfg-domain.md §8.4）。clientId 去重。</summary>
        [HttpPost("reports")]
        public async Task<IActionResult> CreateReport([FromBody] ReportCreateRequest req)
        {
            if (await RequireMobilePermissionAsync("Report") is { } forbidden) return forbidden;
            return await WithConcurrencyRetryAsync(() => CreateReportOnceAsync(req));
        }

        private async Task<IActionResult> CreateReportOnceAsync(ReportCreateRequest req)
        {
            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var session = await _db.ProductionSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == req.SessionId);
            if (session == null)
                return NotFound(new MobileErrorResult(Error: new("SESSION_NOT_FOUND", PzText.Get("Mob_SessionNotFound"))));

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == session.DispatchId);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            // 先依單位捨入再做所有數量判定：整數單位下 0.4 是 0，應被「至少一個大於 0」擋下
            var (qtyOk, qtyNg) = await RoundReportQtyAsync(dispatch, req.QtyOk, req.QtyNg);

            // 任一數量為負一律拒絕：混合正負會穿過下面「皆 ≤ 0」的守門並倒扣 QtyDone（桌機 DispatchController 同一條）
            if (qtyOk < 0 || qtyNg < 0)
                return BadRequest(new MobileErrorResult(Error: new("QTY_NEGATIVE", PzText.Get("Mob_QtyNegative"))));

            if (qtyOk <= 0 && qtyNg <= 0)
                return BadRequest(new MobileErrorResult(Error: new("QTY_REQUIRED", PzText.Get("Mob_QtyRequired"))));

            if (dispatch.Status != DispatchStatus.InProgress)
                return BadRequest(new MobileErrorResult(Error: new("INVALID_STATUS", PzText.Get("Mob_DispatchNotInProduction"))));

            if (await RejectOverReportAsync(dispatch, qtyOk) is { } over) return over;

            // §25.4 工序順序守門（僅簡化模式生效）：與桌機報工共用同一份判定
            if ((await _sequenceGuard.CheckAsync(dispatch, qtyOk)).Blocked)
                return BadRequest(new MobileErrorResult(
                    Error: new("PREV_OPERATION_SHORTAGE", PzText.Get("Mob_PrevOpInsufficient"))));

            var now    = DateTime.UtcNow;
            var (reportedAt, rejectTime) = await ResolveOccurredAtAsync(req.ClientTimestamp, dispatch.PlantId, session.StartAt, now);
            if (rejectTime != null) return rejectTime;

            var (report, boxWarning) = await AddReportCoreAsync(dispatch, req.SessionId,
                qtyOk, qtyNg, req.ContainerId, req.NgReasonCode, now, reportedAt);

            var result = new MobileOkResult(Data: new { report.ReportId },
                Message: WithBoxWarning(PzText.Get("Mob_ReportOk"), boxWarning));
            var saved  = await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, "/api/mobile/reports", result);

            // 觸發 ETA 重算 + Web Push 通知（僅在儲存成功時；背景，桌機/手機共用 IReportEtaNotifier，
            // 自建 DI scope——request-scoped 服務不可被 Task.Run 捕捉）
            if (saved is OkObjectResult && dispatch.PlantId != null)
                _etaNotifier.QueueRecalculate(dispatch.DispatchId, dispatch.MachineCode, dispatch.PlantId, now);

            return saved;
        }

        /// <summary>
        /// 完工：結束本次上工，可一併回報最後一批數量（數量皆 0 時等同純下工）。clientId 去重。
        /// 報工與完工分成兩支：批次轉移的現場每轉一批報一次工但人還在線上，
        /// 只有完工才關閉 session 並結算工時成本；派工是否結案仍依累計數量自動判定。
        /// </summary>
        [HttpPatch("sessions/{sessionId}/complete")]
        public async Task<IActionResult> CompleteSession(string sessionId, [FromBody] SessionCompleteRequest req)
        {
            if (await RequireMobilePermissionAsync("StopWork") is { } forbidden) return forbidden;
            return await WithConcurrencyRetryAsync(() => CompleteSessionOnceAsync(sessionId, req));
        }

        private async Task<IActionResult> CompleteSessionOnceAsync(string sessionId, SessionCompleteRequest req)
        {
            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            var session = await _db.ProductionSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (session == null)
                return NotFound(new MobileErrorResult(Error: new("SESSION_NOT_FOUND", PzText.Get("Mob_SessionNotFound"))));
            if (session.Status != ProductionSessionStatus.Open)
                return BadRequest(new MobileErrorResult(Error: new("SESSION_NOT_OPEN", PzText.Get("Mob_SessionAlreadyClosed"))));

            var dispatch = await _db.Dispatches.FirstOrDefaultAsync(d => d.DispatchId == session.DispatchId);
            if (dispatch == null)
                return NotFound(new MobileErrorResult(Error: new("DISPATCH_NOT_FOUND", PzText.Get("Mob_DispatchNotFound"))));

            // 先捨入再判斷：整數單位下 0.4 是 0，應視為純下工
            var (qtyOk, qtyNg) = await RoundReportQtyAsync(dispatch, req.QtyOk, req.QtyNg);

            // 任一數量為負一律拒絕（同 CreateReport）；要在關閉上工紀錄之前擋
            if (qtyOk < 0 || qtyNg < 0)
                return BadRequest(new MobileErrorResult(Error: new("QTY_NEGATIVE", PzText.Get("Mob_QtyNegative"))));

            var now = DateTime.UtcNow;
            var (endAt, rejectTime) = await ResolveOccurredAtAsync(req.ClientTimestamp, dispatch.PlantId, session.StartAt, now);
            if (rejectTime != null) return rejectTime;

            string? boxWarning = null;
            if (qtyOk > 0 || qtyNg > 0)
            {
                if (dispatch.Status != DispatchStatus.InProgress)
                    return BadRequest(new MobileErrorResult(Error: new("INVALID_STATUS", PzText.Get("Mob_DispatchNotInProduction"))));
                if (await RejectOverReportAsync(dispatch, qtyOk) is { } over) return over;

                // §25.4 工序順序守門（僅簡化模式生效）
                if ((await _sequenceGuard.CheckAsync(dispatch, qtyOk)).Blocked)
                    return BadRequest(new MobileErrorResult(
                        Error: new("PREV_OPERATION_SHORTAGE", PzText.Get("Mob_PrevOpInsufficient"))));

                (_, boxWarning) = await AddReportCoreAsync(dispatch, sessionId,
                    qtyOk, qtyNg, req.ContainerId, req.NgReasonCode, now, endAt);
            }

            session.EndAt     = endAt;
            session.Status    = ProductionSessionStatus.Closed;
            session.UpdatedAt = now;

            // 累計實際人工/機台成本（§16.3，與桌機共用同一份邏輯）
            await _costAccrual.AccrueOnSessionStopAsync(session);

            var result = new MobileOkResult(Message: WithBoxWarning(PzText.Get("Mob_CompleteOk"), boxWarning));
            var saved  = await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp,
                $"/api/mobile/sessions/{sessionId}/complete", result);

            if (saved is OkObjectResult && dispatch.PlantId != null)
                _etaNotifier.QueueRecalculate(dispatch.DispatchId, dispatch.MachineCode, dispatch.PlantId, now);

            return saved;
        }

        /// <summary>
        /// 報工數量依產品庫存單位的小數位數四捨五入（CLAUDE.md〈數量欄位 UOM 小數點〉），
        /// 與桌機 DispatchController.RoundReportQtyAsync 同一條。手機輸入框是 step="any"，不捨入就原值入帳。
        /// </summary>
        private async Task<(decimal QtyOk, decimal QtyNg)> RoundReportQtyAsync(Dispatch dispatch, decimal qtyOk, decimal qtyNg)
        {
            var uom = await _db.WorkOrders.AsNoTracking()
                .Where(w => w.WorkOrderId == dispatch.WorkOrderId)
                .Join(_db.Products.AsNoTracking(), w => w.ProductCode, p => p.ProductCode, (w, p) => p.StockUom)
                .FirstOrDefaultAsync();
            // 與模具打次服務同一個取法（見 AddReportCoreAsync）：不為這一處改建構式，免得連動所有既有測試
            var uomService = HttpContext.RequestServices.GetRequiredService<IUomService>();
            return (await uomService.RoundQtyAsync(uom, qtyOk), await uomService.RoundQtyAsync(uom, qtyNg));
        }

        /// <summary>
        /// 建立報工紀錄＋累計數量／報廢量／模具打次（不 SaveChanges，交由呼叫端一起提交）。
        /// 報工與完工兩支共用，避免同一段業務規則各寫一份而漂移。
        /// 容器欄掃到箱號就裝箱（§9.4）；箱子不能裝時報工照收、箱子不綁，回傳原因讓畫面提示——
        /// 與桌機不同：手機報工可能是斷線補送，擋下來會卡在佇列裡，現場也早就離開了。
        /// </summary>
        private async Task<(ProductionReport Report, string? BoxWarning)> AddReportCoreAsync(
            Dispatch dispatch, string sessionId,
            decimal qtyOk, decimal qtyNg, string? containerId, string? ngReasonCode, DateTime now, DateTime reportedAt)
        {
            var (box, boxWarning) = await ReportContainers.LoadBoxAsync(
                _db, containerId, dispatch, qtyOk, _userContext.UserId, now);

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
                ContainerCode = box?.ContainerCode ?? containerId,
                ReportedAt    = reportedAt,   // 發生時間（斷線補送時早於 now），見 ResolveOccurredAtAsync
                PlantId       = dispatch.PlantId,
                CreatedAt     = now,
                UpdatedAt     = now
            };
            await ReportContainers.AttachAsync(_db, report, box?.ContainerUnitId);
            _db.ProductionReports.Add(report);

            dispatch.QtyDone   += qtyOk;
            dispatch.UpdatedAt  = now;

            if (qtyNg > 0)
            {
                var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
                if (wo != null)
                {
                    wo.QtyScrap  += qtyNg;
                    wo.UpdatedAt  = now;
                }
            }

            if (dispatch.QtyDone >= dispatch.Quantity)
            {
                dispatch.Status = DispatchStatus.Closed;
                await CheckWorkOrderCompletionAsync(dispatch.WorkOrderId, now);
            }

            // 模具打次回寫＋保養門檻檢查（mfg-domain.md §14.1 第二階段），與報工同交易
            await HttpContext.RequestServices
                .GetRequiredService<IToolingShotService>()
                .AccumulateAsync(dispatch, qtyOk + qtyNg, now);

            return (report, boxWarning);
        }

        /// <summary>報工成功訊息後接「箱子沒裝成」的原因（沒有就原訊息）。</summary>
        private static string WithBoxWarning(string message, string? boxWarning)
            => boxWarning == null ? message : PzText.Get("Mob_BoxNotLoadedFmt", message, boxWarning);

        // ── 代碼維護下拉（唯讀）────────────────────────────────────────────────

        /// <summary>
        /// 依類別取代碼維護選項（報工 NG 原因等下拉用）。
        /// 多語由前端依語系自行取 nameZh / nameEn（CodeEntry 慣例）。
        /// </summary>
        [HttpGet("code-entries")]
        public async Task<IActionResult> GetCodeEntries([FromQuery] string category)
        {
            // 代碼清單（不良原因／停機原因…）為多頁共用的下拉來源，走模組層閘門。
            if (await RequireMobileModuleAsync() is { } forbidden) return forbidden;

            if (string.IsNullOrWhiteSpace(category))
                return BadRequest(new MobileErrorResult(Error: new("BAD_REQUEST", PzText.Get("Mob_CategoryRequired"))));

            var items = await _db.CodeEntries.AsNoTracking()
                .Where(e => e.Category == category && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .Select(e => new { e.Code, e.NameZh, e.NameEn })
                .ToListAsync();

            return Ok(new MobileOkResult(Data: items));
        }

        // ── 機台狀態事件 ──────────────────────────────────────────────────────

        /// <summary>
        /// 機台狀態切換事件（mfg-domain.md §13 / mobile-pwa.md §7.4）。clientId 去重。
        /// 副作用：更新 Machine.CurrentStatus；BreakDown → Scheduled 派工退回 Pending；InProgress 派工轉 OnHold。
        /// </summary>
        [HttpPost("machines/{machineCode}/status-event")]
        public async Task<IActionResult> MachineStatusEvent(string machineCode, [FromBody] MachineStatusEventRequest req)
        {
            if (await RequireMobilePermissionAsync("MachineStatus") is { } forbidden) return forbidden;

            var cached = await GetIdempotentResultAsync(req.ClientId);
            if (cached != null) return Ok(cached);

            if (!Enum.TryParse<MachineEventType>(req.EventType, ignoreCase: true, out var eventType))
                return BadRequest(new MobileErrorResult(Error: new("INVALID_EVENT_TYPE", PzText.Get("Mob_UnsupportedEventFmt", req.EventType))));

            var plantId = _userContext.PlantId;

            // 🚨 機台查詢**必須**帶廠別條件。WorkCenterMachine 沒有 PlantId 欄位、也不是
            // IPlantEntity（主鍵是 WorkCenterCode+MachineCode），所以**全域廠別過濾攔不到它**
            // ——廠別是掛在它的 WorkCenter 上。少了這個條件，別廠的機台碼一樣查得到，
            // 接著下面的 `machine.MachineStatus = targetStatus` 會改掉**別廠機台的主檔狀態**，
            // 而 MachineStatusOrder 又用當下情境的 PlantId 存進來——兩邊都成功、都不報錯。
            //
            // 這不是假想的觸發路徑：離線佇列不記廠別，排隊期間切了廠再同步就會走到這裡。
            // 桌機 MachineStatusOrderController 的機台**清單**本來就是這樣寫的
            //（`m.WorkCenter!.PlantId == UserContext.PlantId`），這裡是把寫入端對齊它。
            var machine = await _db.WorkCenterMachines
                .FirstOrDefaultAsync(m => m.MachineCode == machineCode
                                       && m.WorkCenter!.PlantId == plantId);
            if (machine == null)
                return NotFound(new MobileErrorResult(Error: new("MACHINE_NOT_FOUND", PzText.Get("Mob_MachineNotFound"))));

            var targetStatus = GetTargetStatus(eventType, machine.MachineStatus);

            var now = DateTime.UtcNow;
            // 不早於這台機台上一筆事件：補送的事件若排到別人剛登的事件之前，停機區間會算反
            var lastEventAt = await _db.MachineStatusOrderLines.AsNoTracking()
                .Where(l => l.MachineCode == machineCode && l.WorkCenterCode == machine.WorkCenterCode)
                .Select(l => (DateTime?)l.Order!.EventAt)
                .MaxAsync();
            var (eventAt, rejectTime) = await ResolveOccurredAtAsync(req.ClientTimestamp, plantId, lastEventAt, now);
            if (rejectTime != null) return rejectTime;

            var order = new MachineStatusOrder
            {
                OrderId     = Guid.NewGuid().ToString("N"),
                EventType   = eventType,
                EventAt     = eventAt,
                RequestedBy = _userContext.UserId,
                Note        = req.Note,
                PlantId     = plantId,
                CreatedAt   = now,
                UpdatedAt   = now
            };
            _db.MachineStatusOrders.Add(order);

            var line = new MachineStatusOrderLine
            {
                OrderId       = order.OrderId,
                LineSeq       = 1,
                MachineCode   = machineCode,
                WorkCenterCode = machine.WorkCenterCode,
                TargetStatus  = targetStatus,
                SourceDocId   = req.SourceDocId
            };
            _db.MachineStatusOrderLines.Add(line);

            // 更新機台狀態
            machine.MachineStatus = targetStatus;

            // 副作用（mfg-domain.md §13.4）：停用→已排退回待排、已投入轉暫停；恢復可用→因停機暫停的自動恢復。
            // 與桌機 MachineStatusOrderController、模具保養到期共用 DispatchLifecycleService。
            var effect = await HttpContext.RequestServices
                .GetRequiredService<IDispatchLifecycleService>()
                .ApplyMachineStatusAsync(machineCode, plantId, targetStatus, now);
            int affectedScheduled  = effect.Unscheduled;
            int affectedInProgress = effect.Held;

            // 保養完成（HO＋SourceDocId=模具代號）→ 累計打次歸零（mfg-domain.md §14.1 第二階段）
            await HttpContext.RequestServices
                .GetRequiredService<IToolingShotService>()
                .ResetOnMaintenanceClosedAsync(eventType, req.SourceDocId, now);

            var result = new MobileOkResult(
                Data: new { order.OrderId, TargetStatus = targetStatus.ToString(), affectedScheduled, affectedInProgress,
                            affectedResumed = effect.Resumed },
                Message: PzText.Get("Mob_MachineStatusUpdatedFmt", targetStatus));
            return await SaveSyncOrConflictAsync(req.ClientId, req.ClientTimestamp, $"/api/mobile/machines/{machineCode}/status-event", result);
        }

        // ── 甘特圖資料（手機版）──────────────────────────────────────────────

        /// <summary>查詢單台機台目前狀態（手機機台狀態異動入口）。</summary>
        [HttpGet("machines/{machineCode}")]
        public async Task<IActionResult> GetMachine(string machineCode)
        {
            // 與 POST machines/{code}/status-event 同鍵（呼叫端是 machine-status 頁）。
            if (await RequireMobilePermissionAsync("MachineStatus") is { } forbidden) return forbidden;

            // 廠別條件與 POST status-event 必須一致：讀得到卻寫不進去（或反過來）
            // 會變成「畫面查得到這台機台，按下去說找不到」這種查不出原因的回報。
            // 理由詳見該處註解——WorkCenterMachine 不受全域廠別過濾保護。
            var machine = await _db.WorkCenterMachines.AsNoTracking()
                .FirstOrDefaultAsync(m => m.MachineCode == machineCode
                                       && m.WorkCenter!.PlantId == _userContext.PlantId);

            if (machine == null)
                return NotFound(new MobileErrorResult(Error: new("MACHINE_NOT_FOUND", PzText.Get("Mob_MachineNotFound"))));

            return Ok(new MobileOkResult(Data: new
            {
                machine.MachineCode,
                machine.MachineName,
                machine.WorkCenterCode,
                Status = machine.MachineStatus.ToString()
            }));
        }

        /// <summary>手機版甘特圖資料（mfg-domain.md §8.5，與桌機版共用格式）。</summary>
        [HttpGet("schedule")]
        public async Task<IActionResult> GetSchedule([FromQuery] string? dateTo = null)
        {
            if (await RequireMobilePermissionAsync("ScheduleView") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId;
            var endDate = dateTo != null && DateOnly.TryParse(dateTo, out var d)
                ? d : DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

            var dispatches = await _db.Dispatches.AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && d.ScheduledStart.HasValue
                         && DateOnly.FromDateTime(d.ScheduledStart!.Value) <= endDate)
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .ToListAsync();

            var byMachine = dispatches
                .Where(d => d.MachineCode != null)
                .GroupBy(d => d.Operation?.WorkCenterId ?? "")
                .Select(wcGroup => new
                {
                    group = wcGroup.Key,
                    data = wcGroup.GroupBy(d => d.MachineCode!).Select(mGroup => new
                    {
                        label = mGroup.Key,
                        data = mGroup.OrderBy(d => d.ScheduledStart).Select(d => new
                        {
                            timeRange = new[] { d.ScheduledStart, d.ScheduledEnd },
                            val  = d.WorkOrderId,
                            ww   = d.DispatchId,
                            wo   = d.WorkOrderId,
                            wh   = (d.ScheduledEnd.HasValue && d.ScheduledStart.HasValue)
                                   ? (int)(d.ScheduledEnd.Value - d.ScheduledStart.Value).TotalMinutes : 0,
                            st   = d.Status.ToString(),
                            done = (int)d.QtyDone,
                            qty  = (int)d.Quantity,
                            late = d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true,
                            matRisk = d.MaterialRisk
                        }).ToArray()
                    }).ToArray()
                }).ToArray();

            return Ok(new MobileOkResult(Data: byMachine));
        }

        // ── 排程結果查詢（多條件）─────────────────────────────────────────────

        /// <summary>
        /// 多條件排程查詢（手機版 schedule-query.html 使用）。
        /// 回傳扁平派工列表，依排程開始時間排序。
        /// </summary>
        [HttpGet("schedule-query")]
        public async Task<IActionResult> ScheduleQuery(
            [FromQuery] string? workCenterId,
            [FromQuery] string? machineCode,
            [FromQuery] string? workOrderId,
            [FromQuery] string? batchId,
            [FromQuery] string? dateFrom,
            [FromQuery] string? dateTo)
        {
            if (await RequireMobilePermissionAsync("ScheduleView") is { } forbidden) return forbidden;

            var plantId = _userContext.PlantId;

            var q = _db.Dispatches.AsNoTracking()
                .Where(d => d.PlantId == plantId && d.ScheduledStart.HasValue);

            if (!string.IsNullOrWhiteSpace(machineCode))
                q = q.Where(d => d.MachineCode != null && d.MachineCode.Contains(machineCode));

            if (!string.IsNullOrWhiteSpace(workOrderId))
                q = q.Where(d => d.WorkOrderId.Contains(workOrderId));

            if (!string.IsNullOrWhiteSpace(dateFrom) && DateOnly.TryParse(dateFrom, out var df))
            {
                var dfUtc = DateTime.SpecifyKind(df.ToDateTime(new TimeOnly(0, 0)), DateTimeKind.Utc);
                q = q.Where(d => d.ScheduledStart >= dfUtc);
            }

            if (!string.IsNullOrWhiteSpace(dateTo) && DateOnly.TryParse(dateTo, out var dt))
            {
                var dtUtc = DateTime.SpecifyKind(dt.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Utc);
                q = q.Where(d => d.ScheduledStart <= dtUtc);
            }

            var dispatches = await q
                .Include(d => d.Operation)
                    .ThenInclude(op => op!.WorkOrder)
                        .ThenInclude(wo => wo!.ProductionBatch)
                .OrderBy(d => d.ScheduledStart)
                .Take(200)
                .ToListAsync();

            // 工作中心後過濾（來自 WorkOrderOperation，不直接在 Dispatch 上）
            if (!string.IsNullOrWhiteSpace(workCenterId))
                dispatches = dispatches.Where(d => d.Operation?.WorkCenterId == workCenterId).ToList();

            // 生產批後過濾
            if (!string.IsNullOrWhiteSpace(batchId))
                dispatches = dispatches.Where(d =>
                    d.Operation?.WorkOrder?.ProductionBatchId?.Contains(batchId) == true).ToList();

            var rows = dispatches.Select(d => (object)new
            {
                dispatchId    = d.DispatchId,
                workOrderId   = d.WorkOrderId,
                batchId       = d.Operation?.WorkOrder?.ProductionBatchId,
                productCode   = d.Operation?.WorkOrder?.ProductCode,
                operationSeq  = d.OperationSeq,
                operationName = d.Operation?.OperationName,
                workCenterId  = d.Operation?.WorkCenterId,
                machineCode   = d.MachineCode,
                scheduledStart = d.ScheduledStart?.ToString("O"),
                scheduledEnd   = d.ScheduledEnd?.ToString("O"),
                status        = d.Status.ToString(),
                late          = d.Operation?.WorkOrder?.ProductionBatch?.IsPredictedLate == true,
                materialRisk  = d.MaterialRisk,
                toolingCode   = d.ToolingCode,
                qty           = (int)d.Quantity,
                done          = (int)d.QtyDone,
            });

            return Ok(new MobileOkResult(Data: rows));
        }

        // ── 私有輔助 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 超量報工（mfg-domain.md §4.6）：與桌機同一份判定（IOverQuantityPolicy）——廠別容許率內照收，
        /// 超過依參數拒收或限主管。錯誤碼維持 OVER_REPORT（手機佇列與既有測試認這個碼）。
        /// </summary>
        private async Task<IActionResult?> RejectOverReportAsync(Dispatch dispatch, decimal qtyOk)
        {
            if (dispatch.QtyDone + qtyOk <= dispatch.Quantity) return null;
            var policy = HttpContext?.RequestServices?.GetService<IOverQuantityPolicy>();
            var over = policy == null
                ? new OverQtyCheck(false, PzText.Get("Mob_ReportOverQty"))
                : await policy.CheckProductionAsync(dispatch.PlantId, _userContext.ActiveRoleId,
                      dispatch.Quantity, dispatch.QtyDone, qtyOk);
            return over.Allowed ? null
                : BadRequest(new MobileErrorResult(Error: new("OVER_REPORT", over.Message ?? PzText.Get("Mob_ReportOverQty"))));
        }

        /// <summary>
        /// 斷線補送的發生時間（換算見 <see cref="MobileOccurredAt"/>）＋合理性檢查：
        /// 不早於 <paramref name="notBefore"/>（上工時間／該機台上一筆事件）；
        /// 回推到別的日期時，那天所屬的會計期間必須仍可過帳——否則工時成本會落進已關帳的月份。
        /// </summary>
        private async Task<(DateTime At, IActionResult? Reject)> ResolveOccurredAtAsync(
            DateTime clientTimestamp, string? plantId, DateTime? notBefore, DateTime now)
        {
            var at = MobileOccurredAt.Resolve(clientTimestamp,
                Request.Headers[MobileOccurredAt.SentAtHeader].ToString(), now, out var queuedFor);
            if (at == null)
                return (now, BadRequest(new MobileErrorResult(Error: new("OCCURRED_TOO_OLD",
                    PzText.Get("Mob_OccurredTooOldFmt", (int)queuedFor.TotalHours)))));

            var occurred = notBefore is { } nb && at.Value < nb ? nb : at.Value;

            if (plantId != null && occurred < now)
            {
                var clock = HttpContext.RequestServices.GetRequiredService<IDateTimeService>();
                var day   = DateOnly.FromDateTime(clock.ToLocalTime(occurred));
                if (day != DateOnly.FromDateTime(clock.ToLocalTime(now))
                    && HttpContext.RequestServices.GetService<SmartFactory.MES.Modules.ACT.Services.IAccountingPeriodService>() is { } periods)
                {
                    try { await periods.AssertCanPostAsync(day, plantId); }
                    catch (InvalidOperationException ex)
                    {
                        return (now, BadRequest(new MobileErrorResult(Error: new("PERIOD_CLOSED", ex.Message))));
                    }
                }
            }
            return (occurred, null);
        }

        /// <summary>
        /// clientId 去重（mobile-pwa.md §8.3）：若已存在則回傳快照；否則回傳 null。
        /// </summary>
        private async Task<MobileOkResult?> GetIdempotentResultAsync(string clientId)
        {
            var log = await _db.SyncLogs.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ClientId == clientId);

            return log?.ResponseSnapshot != null
                ? JsonSerializer.Deserialize<MobileOkResult>(log.ResponseSnapshot)
                : null;
        }

        /// <summary>
        /// 並發衝突（另一個人剛改了同一張派工／工單，xmin 對不上）時，在<b>同一個請求裡</b>以最新資料重做，最多
        /// <see cref="ServerConflictRetries"/> 次，仍衝突才回 409。
        ///
        /// <para>2026-10-21 換班尖峰壓測（ShiftChangeLoadTests）：同一條生產線多人同時報工／下工，第一次送出約四到五成
        /// 撞 409。手機直接送出時 409 不會自動重送，而是跳「資料已被其他人修改，請重新整理」要人重按——換班時現場會看到一片紅字。
        /// 重做是安全的：每次都從頭讀起（ChangeTracker 清空）；去重檢查在最前面；ETA 重算與推播只在存檔成功後觸發；
        /// SyncLog 與業務變更在同一次存檔，失敗的那一輪什麼都沒寫進去。</para>
        /// </summary>
        private async Task<IActionResult> WithConcurrencyRetryAsync(Func<Task<IActionResult>> once)
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await once();
                var conflict = IsConcurrencyConflict(result);
                if (attempt > 0 && (!conflict || attempt >= ServerConflictRetries))
                    ConcurrencyConflictLog.Mobile(HttpContext, succeeded: !conflict, retries: attempt);
                if (attempt >= ServerConflictRetries || !conflict) return result;
                _db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(10, 40) * (attempt + 1));   // 錯開，不要大家同一刻再撞一次
            }
        }

        public const int ServerConflictRetries = 3;

        private static bool IsConcurrencyConflict(IActionResult result) =>
            result is ConflictObjectResult { Value: MobileErrorResult { Error.Code: "CONCURRENCY_CONFLICT" } };

        /// <summary>寫入 SyncLog 並儲存（含主業務變更）。</summary>
        private async Task SaveWithSyncLogAsync(
            string clientId, DateTime clientTimestamp, string endpoint, MobileOkResult result)
        {
            _db.SyncLogs.Add(new SyncLog
            {
                ClientId          = clientId,
                Endpoint          = endpoint,
                ClientTimestamp   = clientTimestamp,
                CreatedAt         = DateTime.UtcNow,
                ResponseSnapshot  = JsonSerializer.Serialize(result)
            });
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// 儲存（含 SyncLog）並回傳結果；若樂觀並發衝突（Dispatch/WorkOrder 等 xmin 實體被他人先改）
        /// 回 409 Conflict 結構化錯誤封包（CONCURRENCY_CONFLICT），供 PWA 離線佇列「重載後重試」，
        /// 而非裸 500。對齊 CreateReport 既有處理。
        /// </summary>
        private async Task<IActionResult> SaveSyncOrConflictAsync(
            string clientId, DateTime clientTimestamp, string endpoint, MobileOkResult result)
        {
            try
            {
                await SaveWithSyncLogAsync(clientId, clientTimestamp, endpoint, result);
                return Ok(result);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new MobileErrorResult(Error: new("CONCURRENCY_CONFLICT", PzText.Get("Mob_ConcurrencyReload"))));
            }
        }

        /// <summary>依事件代碼決定目標機台狀態（mfg-domain.md §13.3）。</summary>
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

        /// <summary>此派工單所屬工作中心是否為生產線（<c>WorkCenter.IsProductionLine</c>），允許多人同時上工。</summary>
        private async Task<bool> IsProductionLineDispatchAsync(Dispatch dispatch)
        {
            var op = await _db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq);
            if (op?.WorkCenterId == null) return false;

            return await _db.Set<WorkCenter>().AsNoTracking()
                .Where(w => w.WorkCenterCode == op.WorkCenterId)
                .Select(w => w.IsProductionLine)
                .FirstOrDefaultAsync();
        }

        private async Task CheckWorkOrderCompletionAsync(string workOrderId, DateTime now)
        {
            // ⚠️ 呼叫端剛把本次派工設成 Closed、還沒存檔。必須讀「已追蹤實體」（不可 AsNoTracking、
            //    不可投影成純量），否則讀回資料庫裡的舊狀態 InProgress，最後一道工序在手機報完工時
            //    工單永遠不會自動轉 Completed。桌機 DispatchController 同一個坑早已修掉，手機這份複本漏了。
            var allDispatches = await _db.Dispatches
                .Where(d => d.WorkOrderId == workOrderId)
                .ToListAsync();

            // 暫停不算完成；判定與桌機、剩餘量結案共用（DispatchHold.IsWorkOrderDone）
            if (DispatchHold.IsWorkOrderDone(allDispatches))
            {
                var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId);
                if (wo is { Status: WorkOrderStatus.InProgress })
                {
                    wo.Status    = WorkOrderStatus.Completed;
                    wo.UpdatedAt = now;
                }
            }
        }
    }
}
