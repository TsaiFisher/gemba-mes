using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    public interface IApprovalService
    {
        Task<ApprovalRequest> SubmitForApproval(string documentId, string documentType, string templateId, string requesterId);
        Task<bool> ProcessApproval(string requestId, string approverId, string roleId, ApprovalStatus action, string? comment);
        Task<bool> WithdrawRequest(string requestId, string requesterId);
        Task<bool> ReassignApprover(string requestId, string newApproverId, string operatorId, string? reason);
        Task<DocumentApprovalConfig?> GetApprovalConfig(string documentType, string? plantId);

        /// <summary>
        /// 解析 Save 時應落地的 ApprovalStatus：
        ///   - 未設線上簽核（無 config / IsApprovalRequired=false / ApprovalTemplateId=null）→ Approved
        ///   - 已設線上簽核 → Draft（後續按「送審」走流程）
        /// 統一所有 Document 模組 Save 行為（InternalOrder / SalesOrder / SalesReturn / GoodsShipment
        /// / PurchaseRequest / PurchaseOrder / GoodsReceipt / GoodsReturn）。
        /// </summary>
        Task<DocumentApprovalStatus> ResolveSaveStatusAsync(string documentType, string? plantId);

        /// <summary>
        /// 業務文件被刪除時連動取消其進行中的簽核申請。
        /// 將申請單狀態設為 Withdrawn 並寫入稽核 Log；非 Pending 狀態回 false。
        /// 與 WithdrawRequest 的差異：不檢查呼叫者身分（由 Controller 確認權限），允許管理員 / 建立者統一呼叫。
        /// </summary>
        Task<bool> CancelOnDelete(string requestId, string operatorId, string? reason);

        /// <summary>
        /// 「待我簽核」清單（DB 端過濾，依送審時間由舊到新）。
        /// 判定語意與 ProcessApproval 的簽核人驗證一致：
        /// ActiveApproverId 已指派 → 僅該使用者；未指派 → 依當前步驟的 Role/User 目標比對
        /// （Supervisor 型未解析成功者不出現在任何人清單，需管理員重新指派）。
        /// </summary>
        Task<List<ApprovalRequest>> GetMyPendingRequestsAsync(string userId, string? activeRoleId);

        /// <summary>「待我簽核」筆數（導覽列 badge 用），語意同 GetMyPendingRequestsAsync。</summary>
        Task<int> CountMyPendingAsync(string userId, string? activeRoleId);

        /// <summary>
        /// 這個人是否為某張簽核申請的**關係人**（可檢視其內容與歷程）。
        ///
        /// 關係人＝申請人／現任簽核人／當前步驟以角色或個人指派到他／曾在本單簽過
        /// （含代理簽核的被代理人）。曾簽過也算，是為了讓簽完的人能回頭查看自己處理過的單。
        ///
        /// 用途：<c>Approval/Process</c>（GET）與 <c>Approval/History</c> 的讀取閘門。
        /// 這兩支原本只有 <c>[Authorize]</c>（＝只要有登入），任何人拿 requestId 就能看到
        /// 別人的簽核內容與意見。「看全部」是另一條路徑（MenuPermission
        /// <c>AllApprovalRequests</c>），由 Controller 自行 OR，本方法不涉權限表。
        /// </summary>
        Task<bool> CanViewRequestAsync(string requestId, string userId, string? activeRoleId);

        /// <summary>
        /// 代理人設定生效時，將既有 Pending 且卡在委託人身上的申請單改派給代理人
        /// （休假典型情境：先設代理再離開，不能只對新單生效）。
        /// 僅處理可確定簽核人的單（ActiveApproverId＝委託人，或 User 型步驟目標為委託人）；
        /// Role 型步驟整組角色皆可簽，單一成員休假不卡單，不改派。
        /// 回傳實際改派筆數。
        /// </summary>
        Task<int> ApplyDelegationToPendingAsync(string delegatorId, string operatorId);
    }

    public class ApprovalService : IApprovalService
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<ApprovalService> _logger;
        private readonly IEnumerable<IApprovalSyncContributor> _syncContributors;
        private readonly IApprovalNotifier _notifier;
        private readonly IDateTimeService? _dateTime;

        // notifier / dateTimeService 為可選參數：未註冊時分別以空實作、UTC 取代，
        // 既有測試/呼叫端不需調整。dateTimeService 用於代理期間的「今日」判定
        // （使用者時區的日界線，避免 UTC 日期在起訖日當天早晚數小時的誤差）。
        public ApprovalService(
            ApplicationDbContext db,
            ILogger<ApprovalService> logger,
            IEnumerable<IApprovalSyncContributor> syncContributors,
            IApprovalNotifier? notifier = null,
            IDateTimeService? dateTimeService = null)
        {
            _db = db;
            _logger = logger;
            _syncContributors = syncContributors;
            _notifier = notifier ?? NullApprovalNotifier.Instance;
            _dateTime = dateTimeService;
        }

        public async Task<ApprovalRequest> SubmitForApproval(string documentId, string documentType, string templateId, string requesterId)
        {
            var template = await _db.ApprovalTemplates
                .Include(t => t.Steps)
                .FirstOrDefaultAsync(t => t.Id == templateId);

            if (template == null)
                throw new InvalidOperationException(CoreText.Get("Msg_TemplateNotFoundFmt", templateId));

            var orderedSteps = template.Steps.OrderBy(s => s.Sequence).ToList();
            if (orderedSteps.Count == 0)
                throw new InvalidOperationException(CoreText.Get("Msg_TemplateNoStepsFmt", templateId));

            // 取得申請者的 ActiveRoleId（用於 Supervisor 解析）
            var requester = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == requesterId);
            var requesterRoleId = requester?.ActiveRoleId;

            var request = new ApprovalRequest
            {
                DocumentId = documentId,
                DocumentType = documentType,
                CurrentStepId = orderedSteps[0].Id,
                Status = ApprovalStatus.Pending,
                RequesterId = requesterId,
                RequesterRoleId = requesterRoleId
            };

            _db.ApprovalRequests.Add(request);

            // 送審事件寫入歷程，讓 _History 有完整的起點（申請人、送審時間）
            _db.ApprovalLogs.Add(new ApprovalLog
            {
                RequestId = request.Id,
                ApproverId = requesterId,
                ApproverRoleId = requesterRoleId ?? string.Empty,
                Action = ApprovalStatus.Pending,
                Comment = CoreText.Token("ApprovalNote_Submitted"),
                ActionTime = DateTime.UtcNow
            });

            // 申請人視同已簽核：SkipSameApprover 開啟時，輪到本人的步驟自動跳過
            var placement = await PlaceOnStepAsync(request, template, orderedSteps, 0,
                new HashSet<string> { requesterId });

            ApprovalStep? landedStep = null;
            if (placement == null)
            {
                // 所有步驟都被自動跳簽 → 送審即終局核准，回寫來源文件
                request.CurrentStepId = orderedSteps[^1].Id;
                request.Status = ApprovalStatus.Approved;
                await SyncSourceDocument(documentId, documentType, ApprovalStatus.Approved);
            }
            else
            {
                request.CurrentStepId = placement.Step.Id;
                request.ActiveApproverId = placement.ActiveApproverId;
                request.ActiveDelegatorId = placement.DelegatorId;
                landedStep = placement.Step;
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("已建立簽核申請 {RequestId}，文件 {DocumentType}/{DocumentId}，落點步驟 {StepId}，簽核人 {Approver}",
                request.Id, documentType, documentId, request.CurrentStepId,
                request.Status == ApprovalStatus.Approved ? "(全數跳簽，逕行核准)" : request.ActiveApproverId ?? "(依步驟目標)");

            if (landedStep != null)
                await NotifySafeAsync(() => _notifier.NotifyPendingAsync(request, landedStep));
            else
                await NotifySafeAsync(() => _notifier.NotifyFinalizedAsync(request, ApprovalStatus.Approved));

            return request;
        }

        private sealed record StepPlacement(ApprovalStep Step, string? ActiveApproverId, string? DelegatorId);

        /// <summary>
        /// 從 orderedSteps[startIndex] 起找出下一個實際需要簽核的步驟：
        /// 1. 每步先解析自然簽核人（User＝目標使用者、Supervisor＝組織樹解析；Role 無單一目標），
        ///    再套用簽核代理設定（僅對可確定的單一簽核人；Role 步驟整組角色可簽，不適用代理）。
        /// 2. 模板開啟 SkipSameApprover 時，有效簽核人已在 signers（申請人＋先前實際簽核人）
        ///    中的步驟自動跳過並寫入 Skipped 歷程；Role 步驟以「任一 signer 是該角色成員」判定。
        /// 全部步驟跳完回傳 null，呼叫端應將申請單終局核准。
        /// </summary>
        private async Task<StepPlacement?> PlaceOnStepAsync(
            ApprovalRequest request, ApprovalTemplate template,
            List<ApprovalStep> orderedSteps, int startIndex, HashSet<string> signers)
        {
            // Supervisor 解析結果整張申請單相同（依申請人角色），解析一次重複使用
            string? supervisorId = null;
            var supervisorResolved = false;

            for (var i = startIndex; i < orderedSteps.Count; i++)
            {
                var step = orderedSteps[i];

                string? naturalTarget = null;
                if (step.ApproverType == ApproverType.User)
                {
                    naturalTarget = step.TargetUserId;
                }
                else if (step.ApproverType == ApproverType.Supervisor)
                {
                    if (!supervisorResolved)
                    {
                        supervisorId = await ResolveSupervisorAsync(request.RequesterRoleId);
                        supervisorResolved = true;
                    }
                    naturalTarget = supervisorId;
                }

                string? delegatorId = null;
                var effectiveTarget = naturalTarget;
                if (!string.IsNullOrEmpty(naturalTarget))
                {
                    var delegateId = await ResolveDelegateAsync(naturalTarget, request.RequesterId);
                    if (delegateId != null)
                    {
                        effectiveTarget = delegateId;
                        delegatorId = naturalTarget;
                    }
                }

                if (template.SkipSameApprover)
                {
                    string? matchedSigner = null;
                    if (step.ApproverType == ApproverType.Role && !string.IsNullOrEmpty(step.TargetRoleId))
                    {
                        var roleId = step.TargetRoleId;
                        matchedSigner = await _db.UserRoles
                            .Where(ur => ur.RoleId == roleId && signers.Contains(ur.UserId))
                            .Select(ur => ur.UserId)
                            .FirstOrDefaultAsync();
                    }
                    else if (!string.IsNullOrEmpty(effectiveTarget) && signers.Contains(effectiveTarget))
                    {
                        matchedSigner = effectiveTarget;
                    }

                    if (matchedSigner != null)
                    {
                        _db.ApprovalLogs.Add(new ApprovalLog
                        {
                            RequestId = request.Id,
                            ApproverId = matchedSigner,
                            ApproverRoleId = step.TargetRoleId ?? string.Empty,
                            Action = ApprovalStatus.Skipped,
                            Comment = matchedSigner == request.RequesterId
                                ? CoreText.Token("ApprovalNote_SkipRequester")
                                : CoreText.Token("ApprovalNote_SkipSameSigner"),
                            OnBehalfOfUserId = matchedSigner == effectiveTarget ? delegatorId : null,
                            ActionTime = DateTime.UtcNow
                        });
                        _logger.LogInformation("申請單 {RequestId} 步驟 {StepId}（Seq {Seq}）自動跳簽：{Signer}",
                            request.Id, step.Id, step.Sequence, matchedSigner);
                        continue;
                    }
                }

                // 落點：Supervisor 型以解析（含代理）結果為準；User 型僅在套用代理時覆寫
                // （無代理時維持 null，由步驟目標比對，與既有語意一致）；Role 型恆為 null。
                var activeApprover = step.ApproverType == ApproverType.Supervisor
                    ? effectiveTarget
                    : delegatorId != null ? effectiveTarget : null;
                return new StepPlacement(step, activeApprover, delegatorId);
            }

            return null;
        }

        /// <summary>
        /// 解析使用者當前生效的簽核代理人：IsActive 且今日（使用者時區）落在起訖區間。
        /// 多筆生效時取最近建立者。迴避原則：代理人為申請人本人時不套用（否則形同自己核自己的單）。
        /// 單層代理，不遞迴展開。
        /// </summary>
        private async Task<string?> ResolveDelegateAsync(string delegatorId, string? requesterId)
        {
            var today = DateOnly.FromDateTime(_dateTime?.Now ?? DateTime.UtcNow);
            var delegateId = await _db.ApprovalDelegations.AsNoTracking()
                .Where(d => d.DelegatorId == delegatorId && d.IsActive
                         && d.StartDate <= today && d.EndDate >= today)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => d.DelegateId)
                .FirstOrDefaultAsync();

            if (string.IsNullOrEmpty(delegateId) || delegateId == delegatorId) return null;

            if (delegateId == requesterId)
            {
                _logger.LogWarning("代理解析：{Delegator} 的代理人 {Delegate} 為申請人本人，不套用代理（迴避原則）",
                    delegatorId, delegateId);
                return null;
            }
            return delegateId;
        }

        /// <summary>通知一律 best-effort：管道故障只記 log，不中斷簽核流程。</summary>
        private async Task NotifySafeAsync(Func<Task> notify)
        {
            try { await notify(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "簽核通知發送失敗（不影響簽核流程）");
            }
        }

        /// <summary>
        /// 解析直屬主管：找申請者 ActiveRole 對應的 OrganizationUnit → ParentId → 對應 Role → 該 Role 名下 active user。
        /// 多人時取 UserName 字典序最小者；找不到回 null（保留 ActiveApproverId = null，需人工指派）。
        /// </summary>
        private async Task<string?> ResolveSupervisorAsync(string? requesterRoleId)
        {
            if (string.IsNullOrEmpty(requesterRoleId)) return null;

            var requesterRole = await _db.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == requesterRoleId);
            if (requesterRole?.OrganizationUnitId == null) return null;

            var requesterOu = await _db.OrganizationUnits.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == requesterRole.OrganizationUnitId);
            if (requesterOu?.ParentId == null) return null;

            // 找掛在 ParentOU 上的所有 Role，撈出有 active user 的最早一位
            var parentRoleIds = await _db.Roles.AsNoTracking()
                .Where(r => r.OrganizationUnitId == requesterOu.ParentId)
                .Select(r => r.Id)
                .ToListAsync();
            if (parentRoleIds.Count == 0) return null;

            var supervisor = await _db.Users.AsNoTracking()
                .Where(u => u.ActiveRoleId != null && parentRoleIds.Contains(u.ActiveRoleId))
                .OrderBy(u => u.UserName)
                .Select(u => u.Id)
                .FirstOrDefaultAsync();

            return supervisor; // 可能為 null（parent OU 無任何 active user）
        }

        public async Task<bool> ProcessApproval(string requestId, string approverId, string roleId, ApprovalStatus action, string? comment)
        {
            var request = await _db.ApprovalRequests.FindAsync(requestId);
            if (request == null)
            {
                _logger.LogWarning("ProcessApproval 找不到申請單 {RequestId}", requestId);
                return false;
            }

            // 僅 Pending 可簽：擋掉已撤回/已駁回/已核准後殘留的簽核表單再次 POST
            // （否則撤回後的申請單可被直接翻成核准，繞過整個流程）
            if (request.Status != ApprovalStatus.Pending)
            {
                _logger.LogWarning("ProcessApproval 狀態不符：申請單 {RequestId} 目前為 {Status}，拒絕操作", requestId, request.Status);
                return false;
            }

            var currentStep = await _db.ApprovalSteps.FindAsync(request.CurrentStepId);
            if (currentStep == null)
            {
                _logger.LogWarning("ProcessApproval 找不到步驟 {StepId}，申請單 {RequestId}", request.CurrentStepId, requestId);
                return false;
            }

            // 簽核人驗證（與 PendingIndex「待我簽核」判定語意一致）：
            // 非當前步驟指定的簽核人/角色不得執行，防止任意登入者直接 POST 核准他人單據。
            bool isAuthorized = currentStep.ApproverType switch
            {
                ApproverType.Role => string.IsNullOrEmpty(request.ActiveApproverId)
                    ? currentStep.TargetRoleId == roleId
                    : request.ActiveApproverId == approverId,
                ApproverType.User => string.IsNullOrEmpty(request.ActiveApproverId)
                    ? currentStep.TargetUserId == approverId
                    : request.ActiveApproverId == approverId,
                ApproverType.Supervisor => !string.IsNullOrEmpty(request.ActiveApproverId)
                                        && request.ActiveApproverId == approverId,
                _ => false
            };
            if (!isAuthorized)
            {
                _logger.LogWarning("ProcessApproval 權限不符：申請單 {RequestId}，步驟 {StepId}，嘗試簽核人 {ApproverId}（角色 {RoleId}）",
                    requestId, currentStep.Id, approverId, roleId);
                return false;
            }

            var log = new ApprovalLog
            {
                RequestId = requestId,
                ApproverId = approverId,
                ApproverRoleId = roleId,
                Action = action,
                Comment = comment,
                ActionTime = DateTime.UtcNow,
                // 代理簽核稽核：操作者是被指派的代理人時，記錄被代理的原簽核人
                OnBehalfOfUserId = !string.IsNullOrEmpty(request.ActiveDelegatorId)
                                && approverId == request.ActiveApproverId
                    ? request.ActiveDelegatorId
                    : null
            };
            _db.ApprovalLogs.Add(log);

            if (action == ApprovalStatus.Rejected)
            {
                request.Status = ApprovalStatus.Rejected;
                _logger.LogInformation("申請單 {RequestId} 已被 {ApproverId} 駁回", requestId, approverId);
            }
            else if (action == ApprovalStatus.ReturnedForRevision)
            {
                request.Status = ApprovalStatus.ReturnedForRevision;
                _logger.LogInformation("申請單 {RequestId} 已由 {ApproverId} 退回修改", requestId, approverId);
            }
            ApprovalStep? advancedToStep = null;
            if (action == ApprovalStatus.Approved)
            {
                var template = await _db.ApprovalTemplates.AsNoTracking()
                    .Include(t => t.Steps)
                    .FirstOrDefaultAsync(t => t.Id == currentStep.TemplateId);

                StepPlacement? placement = null;
                if (template != null)
                {
                    var remainingSteps = template.Steps
                        .Where(s => s.Sequence > currentStep.Sequence)
                        .OrderBy(s => s.Sequence)
                        .ToList();

                    // 實際簽核人集合：申請人（送審視同簽核）＋歷程中已核准者＋本次簽核人
                    var signers = new HashSet<string> { approverId };
                    if (!string.IsNullOrEmpty(request.RequesterId)) signers.Add(request.RequesterId);
                    var priorApprovers = await _db.ApprovalLogs.AsNoTracking()
                        .Where(l => l.RequestId == requestId && l.Action == ApprovalStatus.Approved)
                        .Select(l => l.ApproverId)
                        .ToListAsync();
                    foreach (var s in priorApprovers) signers.Add(s);

                    placement = await PlaceOnStepAsync(request, template, remainingSteps, 0, signers);
                }

                if (placement == null)
                {
                    request.Status = ApprovalStatus.Approved;
                    _logger.LogInformation("申請單 {RequestId} 已全部通過，由 {ApproverId} 完成最終簽核", requestId, approverId);
                }
                else
                {
                    request.CurrentStepId = placement.Step.Id;
                    request.ActiveApproverId = placement.ActiveApproverId;
                    request.ActiveDelegatorId = placement.DelegatorId;
                    advancedToStep = placement.Step;
                    _logger.LogInformation("申請單 {RequestId} 步驟推進至 {NextStepId}（Seq {Seq}），簽核人 {Approver}",
                        requestId, placement.Step.Id, placement.Step.Sequence, request.ActiveApproverId ?? "(依步驟目標)");
                }
            }

            // 最終狀態 → 回寫來源文件
            if (request.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.ReturnedForRevision)
                await SyncSourceDocument(request.DocumentId, request.DocumentType, request.Status);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // xmin 比對失敗＝另一位簽核人（或連點）已先完成操作，後到者不落地
                _logger.LogWarning("ProcessApproval 並發衝突：申請單 {RequestId} 已被其他操作先行處理，{ApproverId} 的動作未落地",
                    requestId, approverId);
                return false;
            }

            if (advancedToStep != null)
                await NotifySafeAsync(() => _notifier.NotifyPendingAsync(request, advancedToStep));
            else if (request.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.ReturnedForRevision)
                await NotifySafeAsync(() => _notifier.NotifyFinalizedAsync(request, request.Status));

            return true;
        }

        /// <summary>
        /// 核准/駁回/退回完成後，回寫各業務文件的狀態欄位。
        /// 先呼叫所有已註冊的 IApprovalSyncContributor（顧問模組擴充點），
        /// 再 fallback 至 Core 內建的 switch 表（目前只剩 CodeEntry）。
        /// </summary>
        private async Task SyncSourceDocument(string documentId, string documentType, ApprovalStatus finalStatus)
        {
            // 1. 顧問模組註冊的 Contributor 優先
            var contributor = _syncContributors.FirstOrDefault(c => c.DocumentType == documentType);
            if (contributor != null)
            {
                await contributor.SyncAsync(_db, documentId, finalStatus);
                return;
            }

            // 2. Core 內建 fallback
            switch (documentType)
            {
                case "CodeEntry":
                    // DocumentId 為 "Category/Code" 自然鍵（CodeEntryController 送審格式）；
                    // 更早的歷史申請單記的是 UUID，保留 Id 比對相容
                    CodeEntry? entry;
                    var slash = documentId.IndexOf('/');
                    if (slash > 0)
                    {
                        var category = documentId[..slash];
                        var code = documentId[(slash + 1)..];
                        entry = await _db.CodeEntries.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(c => c.Category == category && c.Code == code);
                    }
                    else
                    {
                        entry = await _db.CodeEntries.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(c => c.Id == documentId);
                    }
                    if (entry != null)
                    {
                        entry.ApprovalStatus = finalStatus switch
                        {
                            ApprovalStatus.Approved            => DocumentApprovalStatus.Approved,
                            ApprovalStatus.ReturnedForRevision => DocumentApprovalStatus.Draft,
                            _                                  => DocumentApprovalStatus.Rejected
                        };
                        entry.IsActive = finalStatus == ApprovalStatus.Approved;
                        // 退回修改：清除申請單 ID，讓申請人可重新送審
                        if (finalStatus == ApprovalStatus.ReturnedForRevision)
                            entry.ApprovalRequestId = null;
                    }
                    break;
            }
        }

        public async Task<bool> WithdrawRequest(string requestId, string requesterId)
        {
            var request = await _db.ApprovalRequests.FindAsync(requestId);
            if (request == null || request.Status != ApprovalStatus.Pending)
                return false;
            if (request.RequesterId != requesterId)
            {
                _logger.LogWarning("WithdrawRequest 權限不符：申請單 {RequestId}，嘗試撤回人 {RequesterId}", requestId, requesterId);
                return false;
            }

            request.Status = ApprovalStatus.Withdrawn;

            _db.ApprovalLogs.Add(new ApprovalLog
            {
                RequestId = requestId,
                ApproverId = requesterId,
                ApproverRoleId = string.Empty,
                Action = ApprovalStatus.Withdrawn,
                Comment = CoreText.Token("ApprovalNote_Withdrawn"),
                ActionTime = DateTime.UtcNow
            });

            // v1.2+: 觸發中央同步機制，將業務文件狀態回寫為 Draft
            await SyncSourceDocument(request.DocumentId, request.DocumentType, ApprovalStatus.Withdrawn);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("WithdrawRequest 並發衝突：申請單 {RequestId} 已被其他操作先行處理", requestId);
                return false;
            }
            _logger.LogInformation("申請單 {RequestId} 已由 {RequesterId} 撤回", requestId, requesterId);
            return true;
        }

        public async Task<bool> ReassignApprover(string requestId, string newApproverId, string operatorId, string? reason)
        {
            var request = await _db.ApprovalRequests.FindAsync(requestId);
            if (request == null || request.Status != ApprovalStatus.Pending)
                return false;

            // 新簽核人必須是實際存在的使用者，避免指派到打錯/已刪除的 ID 讓申請單永遠隱形
            var newApprover = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == newApproverId);
            if (newApprover == null)
            {
                _logger.LogWarning("ReassignApprover 找不到新簽核人 {NewApproverId}，申請單 {RequestId}", newApproverId, requestId);
                return false;
            }

            request.ActiveApproverId = newApproverId;
            request.ActiveDelegatorId = null; // 管理員明確指派，清除代理脈絡

            _db.ApprovalLogs.Add(new ApprovalLog
            {
                RequestId = requestId,
                ApproverId = operatorId,
                ApproverRoleId = string.Empty,
                Action = ApprovalStatus.Pending,
                Comment = reason == null
                    ? CoreText.Token("ApprovalNote_ReassignedDefaultFmt", newApproverId)
                    : CoreText.Token("ApprovalNote_ReassignedFmt", newApproverId, reason),
                ActionTime = DateTime.UtcNow
            });

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("ReassignApprover 並發衝突：申請單 {RequestId} 已被其他操作先行處理", requestId);
                return false;
            }
            _logger.LogInformation("申請單 {RequestId} 已由 {OperatorId} 重新指派簽核人為 {NewApproverId}", requestId, operatorId, newApproverId);

            var currentStep = await _db.ApprovalSteps.FindAsync(request.CurrentStepId);
            if (currentStep != null)
                await NotifySafeAsync(() => _notifier.NotifyPendingAsync(request, currentStep));
            return true;
        }

        public async Task<int> ApplyDelegationToPendingAsync(string delegatorId, string operatorId)
        {
            var delegateId = await ResolveDelegateAsync(delegatorId, requesterId: null);
            if (delegateId == null) return 0;

            var names = await _db.Users.AsNoTracking()
                .Where(u => u.Id == delegatorId || u.Id == delegateId)
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            // 代理是跟著「人」走的全域設定：改派需涵蓋委託人在所有廠別/資料範圍的待簽單，
            // 故略過全域過濾器（僅以身分條件鎖定，不放寬任何狀態條件）。
            // 注意：join 內不可 AsNoTracking——該旗標作用於整條查詢，會讓 r 的改派寫不回去。
            var candidates = await _db.ApprovalRequests.IgnoreQueryFilters()
                .Where(r => r.Status == ApprovalStatus.Pending)
                .Join(_db.ApprovalSteps, r => r.CurrentStepId, s => s.Id, (r, s) => new { r, s })
                .Where(x => x.r.ActiveApproverId == delegatorId
                         || (string.IsNullOrEmpty(x.r.ActiveApproverId)
                             && x.s.ApproverType == ApproverType.User
                             && x.s.TargetUserId == delegatorId))
                .ToListAsync();

            var applied = new List<(ApprovalRequest Request, ApprovalStep Step)>();
            foreach (var x in candidates)
            {
                if (x.r.RequesterId == delegateId) continue; // 迴避：代理人是該單申請人

                x.r.ActiveApproverId = delegateId;
                x.r.ActiveDelegatorId = delegatorId;
                _db.ApprovalLogs.Add(new ApprovalLog
                {
                    RequestId = x.r.Id,
                    ApproverId = operatorId,
                    ApproverRoleId = string.Empty,
                    Action = ApprovalStatus.Pending,
                    Comment = CoreText.Token("ApprovalNote_DelegationAppliedFmt",
                                  names.GetValueOrDefault(delegatorId, delegatorId), names.GetValueOrDefault(delegateId, delegateId)),
                    ActionTime = DateTime.UtcNow
                });
                applied.Add((x.r, x.s));
            }
            if (applied.Count == 0) return 0;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // 任一單被並發先行處理即整批未落地；代理設定仍在，重新儲存設定可再觸發
                _logger.LogWarning("ApplyDelegationToPending 並發衝突：{Delegator} 的待簽單改派未落地", delegatorId);
                return 0;
            }

            _logger.LogInformation("代理人設定生效：{Delegator} → {Delegate}，改派 {Count} 張待簽單",
                delegatorId, delegateId, applied.Count);

            foreach (var (req, step) in applied)
                await NotifySafeAsync(() => _notifier.NotifyPendingAsync(req, step));

            return applied.Count;
        }

        public async Task<bool> CancelOnDelete(string requestId, string operatorId, string? reason)
        {
            var request = await _db.ApprovalRequests.FindAsync(requestId);
            if (request == null || request.Status != ApprovalStatus.Pending)
                return false;

            request.Status = ApprovalStatus.Withdrawn;

            _db.ApprovalLogs.Add(new ApprovalLog
            {
                RequestId = requestId,
                ApproverId = operatorId,
                ApproverRoleId = string.Empty,
                Action = ApprovalStatus.Withdrawn,
                Comment = reason == null
                    ? CoreText.Token("ApprovalNote_DocDeletedDefault")
                    : CoreText.Token("ApprovalNote_DocDeletedFmt", reason),
                ActionTime = DateTime.UtcNow
            });

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("CancelOnDelete 並發衝突：申請單 {RequestId} 已被其他操作先行處理", requestId);
                return false;
            }
            _logger.LogInformation("申請單 {RequestId} 已由 {OperatorId} 隨文件刪除自動取消", requestId, operatorId);
            return true;
        }

        public async Task<DocumentApprovalConfig?> GetApprovalConfig(string documentType, string? plantId)
        {
            return await _db.DocumentApprovalConfigs
                .Include(c => c.ApprovalTemplate)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.DocumentType == documentType && c.PlantId == plantId);
        }

        public async Task<DocumentApprovalStatus> ResolveSaveStatusAsync(string documentType, string? plantId)
        {
            var config = await GetApprovalConfig(documentType, plantId);
            var requiresApproval = config?.IsApprovalRequired == true
                                && !string.IsNullOrEmpty(config.ApprovalTemplateId);
            return requiresApproval
                ? DocumentApprovalStatus.Draft
                : DocumentApprovalStatus.Approved;
        }

        public Task<List<ApprovalRequest>> GetMyPendingRequestsAsync(string userId, string? activeRoleId)
            => MyPendingQuery(userId, activeRoleId)
                .OrderBy(r => r.CreatedAt) // 等最久的排最前
                .ToListAsync();

        public Task<int> CountMyPendingAsync(string userId, string? activeRoleId)
            => MyPendingQuery(userId, activeRoleId).CountAsync();

        public async Task<bool> CanViewRequestAsync(string requestId, string userId, string? activeRoleId)
        {
            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(userId)) return false;

            // 申請人／現任簽核人／未指派但當前步驟指到我（語意對齊 MyPendingQuery，
            // 但不限 Pending——已完成的單，關係人仍應看得到歷程）
            var isParty = await _db.ApprovalRequests.AsNoTracking()
                .Where(r => r.Id == requestId)
                .Join(_db.ApprovalSteps.AsNoTracking(), r => r.CurrentStepId, s => s.Id,
                      (r, s) => new { r, s })
                .AnyAsync(x => x.r.RequesterId == userId
                            || x.r.ActiveApproverId == userId
                            || (string.IsNullOrEmpty(x.r.ActiveApproverId)
                                && ((x.s.ApproverType == ApproverType.Role && x.s.TargetRoleId == activeRoleId)
                                 || (x.s.ApproverType == ApproverType.User && x.s.TargetUserId == userId))));
            if (isParty) return true;

            // 曾在本單簽過（含代理簽核的被代理人）
            return await _db.ApprovalLogs.AsNoTracking()
                .AnyAsync(l => l.RequestId == requestId
                            && (l.ApproverId == userId || l.OnBehalfOfUserId == userId));
        }

        /// <summary>
        /// 「待我簽核」DB 端查詢，語意與 ProcessApproval 的簽核人驗證一致：
        /// ActiveApproverId 已指派（主管解析/重新指派）→ 僅該使用者；
        /// 未指派 → 依當前步驟 Role/User 目標比對；Supervisor 型未解析 → 不屬於任何人。
        /// 廠別與層級權限由全域查詢過濾器套用（簽核作用範圍＝單一公司別／廠別，設計決策）。
        /// </summary>
        private IQueryable<ApprovalRequest> MyPendingQuery(string userId, string? activeRoleId)
            => _db.ApprovalRequests.AsNoTracking()
                .Where(r => r.Status == ApprovalStatus.Pending)
                .Join(_db.ApprovalSteps.AsNoTracking(),
                      r => r.CurrentStepId, s => s.Id,
                      (r, s) => new { r, s })
                .Where(x => x.r.ActiveApproverId == userId
                         || (string.IsNullOrEmpty(x.r.ActiveApproverId)
                             && ((x.s.ApproverType == ApproverType.Role && x.s.TargetRoleId == activeRoleId)
                              || (x.s.ApproverType == ApproverType.User && x.s.TargetUserId == userId))))
                .Select(x => x.r);
    }
}
