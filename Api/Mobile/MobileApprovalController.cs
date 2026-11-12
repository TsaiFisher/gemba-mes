using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機版「待我簽核」。
    ///
    /// <para><b>2026-09-09 起可以在手機上簽了，但只限有摘要的單別。</b>
    /// 這一段原本寫的是「刻意不提供核准／駁回」，理由是
    /// <see cref="ApprovalRequest"/> 上只有單號、型別、申請人、時間、步驟，
    /// **沒有金額、沒有品項、沒有摘要**，而手機端一個 Details 頁都沒有——
    /// 在那種狀態下放一顆「核准」等於做出一個看不到內容卻按得下去的按鈕。
    /// 那個理由沒有變，變的是缺的東西補上了：
    /// <see cref="IApprovalSummaryProvider"/> 一個 DocumentType 一份摘要。</para>
    ///
    /// <para>⚠️ <b>所以「能不能簽」是由「有沒有摘要」決定的，不是由 API 決定的。</b>
    /// 沒有 provider 的單別，<c>pending</c> 會回 <c>canDecideOnMobile = false</c>，
    /// <c>summary</c> 回 <c>supported = false</c>，而 <c>decide</c> 直接回
    /// <c>NOT_SUPPORTED_ON_MOBILE</c>——三層都擋，因為前端一有 bug
    /// 就會出現「看不到內容卻簽下去」，那正是這整個設計要防的事。
    /// 哪些單別已實作、哪些刻意還沒做，由 <c>ApprovalSummaryCoverageTests</c> 釘住。</para>
    ///
    /// <para><b>刻意不掛手機模組閘門</b>（<c>MobileModules</c>／<c>MobileModuleActions</c>）。
    /// 桌機的 <c>ApprovalController</c> 只有 <c>[Authorize]</c>，看得到什麼完全由
    /// 「這張單是不是指到我」決定。改用手機模組授權會讓同一個人「桌機簽得了、手機看不到」，
    /// 而 <c>RequireMobileModuleAsync</c> 的 403 症狀是**清單空白**——與「真的沒有待簽核」
    /// 長得一模一樣（§26 FPI 有 Web／Mobile 漂移前科）。這裡與桌機採同一把鑰匙。</para>
    ///
    /// <para>⚠️ <b>這條路徑不得進離線佇列。</b>佇列的語意是「晚一點送出，結果一樣」，
    /// 簽核不是：排隊期間單據可能被撤回、被別人簽掉、或步驟已推進，
    /// 而 <c>ProcessApproval</c> 對這些一律 <c>return false</c>——使用者會在幾小時後
    /// 收到一則「簽核失敗」而完全不知道發生什麼事。
    /// 原本這裡只有 GET，天然不會被佇列收走；現在多了 <c>decide</c> 這支 POST，
    /// 所以改由 <c>offline-queue.js</c> 明文排除本路徑，並有 jest 測試釘住。</para>
    /// </summary>
    [Route("api/mobile/approval")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileApprovalController : ControllerBase
    {
        private readonly IApprovalService _approvalService;
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;

        /// <summary>
        /// DocumentType → 摘要 provider。**同一個 DocumentType 註冊兩次是設定錯誤**，
        /// 但 ToDictionary 會直接拋——那會讓每一支 API 都 500，而症狀（整個手機簽核壞掉）
        /// 與病因（某個模組多註冊了一次）距離很遠。所以這裡取第一個並讓建置期的
        /// ApprovalSummaryCoverageTests 去管唯一性。
        /// </summary>
        private readonly Dictionary<string, IApprovalSummaryProvider> _summaries;

        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private readonly IApprovalDocumentTypeLabels _docTypeLabels;

        public MobileApprovalController(
            IApprovalService approvalService,
            ApplicationDbContext db,
            UserManager<ApplicationUser> userManager,
            IEnumerable<IApprovalSummaryProvider> summaries,
            IStringLocalizer<ProjectZSharedResource> localizer,
            IApprovalDocumentTypeLabels docTypeLabels)
        {
            _docTypeLabels = docTypeLabels;
            _approvalService = approvalService;
            _db = db;
            _userManager = userManager;
            _localizer = localizer;
            _summaries = summaries
                .GroupBy(p => p.DocumentType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        /// <summary>待我簽核的筆數（首頁 badge 用）。</summary>
        [HttpGet("count")]
        [PermissionGateExempt(
            "與桌機的 ApprovalController 同一把鑰匙：那支只有 [Authorize]，沒有 MenuKey 守門，"
            + "看得到什麼完全由 MyPendingQuery 的『這張單有沒有指到我』決定（外加廠別／資料層級的全域過濾）。"
            + "在手機這一側額外加閘門，會做出『桌機簽得了、手機看不到』的漂移，"
            + "而它的症狀是清單空白——與『真的沒有待簽核』分不出來。")]
        public async Task<IActionResult> GetCount()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            return Ok(new { count = await _approvalService.CountMyPendingAsync(user.Id, user.ActiveRoleId) });
        }

        /// <summary>
        /// 待我簽核的清單。判定語意完全沿用 <see cref="IApprovalService.GetMyPendingRequestsAsync"/>，
        /// 與桌機 <c>PendingIndex</c> 和 <c>ProcessApproval</c> 是同一份規則——
        /// 手機自己再寫一份「哪些算待我簽核」的條件，遲早會與能不能真的簽不一致。
        /// </summary>
        [HttpGet("pending")]
        [PermissionGateExempt(
            "與桌機的 ApprovalController 同一把鑰匙：那支只有 [Authorize]，沒有 MenuKey 守門，"
            + "看得到什麼完全由 MyPendingQuery 的『這張單有沒有指到我』決定（外加廠別／資料層級的全域過濾）。"
            + "在手機這一側額外加閘門，會做出『桌機簽得了、手機看不到』的漂移，"
            + "而它的症狀是清單空白——與『真的沒有待簽核』分不出來。")]
        public async Task<IActionResult> GetPending()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var requests = await _approvalService.GetMyPendingRequestsAsync(user.Id, user.ActiveRoleId);

            var stepIds = requests.Select(r => r.CurrentStepId).Distinct().ToList();
            var steps = await _db.ApprovalSteps.AsNoTracking()
                .Where(s => stepIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);

            var requesterIds = requests.Select(r => r.RequesterId)
                .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToList();
            var names = requesterIds.Count == 0
                ? new Dictionary<string, string>()
                : await _userManager.Users.AsNoTracking()
                    .Where(u => requesterIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName })
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            return Ok(new
            {
                items = requests.Select(r => new
                {
                    id = r.Id,
                    documentType = r.DocumentType,
                    // 與桌機簽核畫面同一份名稱（IApprovalDocumentTypeLabels），依請求語系
                    documentTypeLabel = _docTypeLabels.Get(r.DocumentType),
                    documentId = r.DocumentId,
                    requester = r.RequesterId != null && names.TryGetValue(r.RequesterId, out var n) ? n : r.RequesterId,
                    createdAt = r.CreatedAt,
                    stepSequence = steps.TryGetValue(r.CurrentStepId, out var s) ? s.Sequence : (int?)null,

                    // 桌機頁的網址。手機沒有任何單據 Details 頁，所以這裡給的是**桌機**連結，
                    // 由畫面另開分頁開啟——不是導覽現有視窗，否則 PWA 會被帶離手機殼而回不來。
                    // null＝這個單別沒有登錄連結；畫面必須把它顯示成「沒有連結」而不是讓點擊靜靜失效。
                    detailsUrl = ApprovalDocumentLinks.Resolve(r.DocumentType, r.DocumentId),

                    // 這個單別在手機上簽不簽得了。false ＝畫面要顯示「請用電腦簽核」
                    // 並給出 detailsUrl，**不可以**顯示一顆核准鈕。
                    canDecideOnMobile = _summaries.ContainsKey(r.DocumentType),
                })
            });
        }

        /// <summary>
        /// 這張待簽核的單，簽核人該看到什麼。
        ///
        /// <para>沒有 <see cref="IApprovalSummaryProvider"/> 的單別回
        /// <c>supported = false</c>，畫面顯示「請用電腦簽核」＋桌機連結——
        /// 這是**刻意的降級**，見 <c>IApprovalSummaryProvider</c> 的註解。</para>
        /// </summary>
        [HttpGet("{requestId}/summary")]
        [PermissionGateExempt(
            "與 pending 同一把鑰匙：看得到什麼完全由『這張單有沒有指到我』決定。"
            + "本動作在回傳摘要前會再驗一次這張單確實在該使用者的待簽核清單裡，"
            + "所以拿到別人的 requestId 也讀不到內容。")]
        public async Task<IActionResult> GetSummary(string requestId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            // 🚨 **一定要用「待我簽核」那份清單來授權，不可以直接 FindAsync。**
            //    直接查的話，任何登入者拿到 requestId 就讀得到別人單據的金額與明細——
            //    桌機的 Process GET 正是踩過這個洞（見 ApprovalController.CanViewRequestAsync
            //    的註解：「本 GET 原本只有 [Authorize]，任何人拿 requestId 就看得到」）。
            //    這裡用的是與 ProcessApproval 同一份判定，所以「讀得到」與「簽得了」一致。
            var mine = await _approvalService.GetMyPendingRequestsAsync(user.Id, user.ActiveRoleId);
            var request = mine.FirstOrDefault(r => r.Id == requestId);
            if (request == null) return NotFound(new { error = "NOT_PENDING_FOR_YOU" });

            var detailsUrl = ApprovalDocumentLinks.Resolve(request.DocumentType, request.DocumentId);

            if (!_summaries.TryGetValue(request.DocumentType, out var provider))
                return Ok(new
                {
                    supported = false,
                    documentType = request.DocumentType,
                    documentId = request.DocumentId,
                    detailsUrl,
                });

            var summary = await provider.GetAsync(_db, request.DocumentId);
            if (summary == null)
                // 單據查不到：已刪除，或被全域廠別／資料權限過濾擋掉。
                // 兩者都不該顯示成空白摘要配一顆核准鈕。
                return Ok(new
                {
                    supported = false,
                    documentType = request.DocumentType,
                    documentId = request.DocumentId,
                    detailsUrl,
                    unavailable = true,
                });

            return Ok(new
            {
                supported = true,
                documentType = request.DocumentType,
                documentId = summary.DocumentId,
                detailsUrl,
                plantId = summary.PlantId,
                counterparty = summary.Counterparty,
                amountLocal = summary.AmountLocal,
                currencyCode = summary.CurrencyCode,
                rows = summary.Rows.Select(r => new { label = r.Label, value = r.Value }),
                lines = summary.Lines.Select(l => new
                {
                    description = l.Description, qty = l.Qty, uomCode = l.UomCode, amount = l.Amount,
                }),
                totalLineCount = summary.TotalLineCount,
            });
        }

        /// <summary>
        /// 在手機上核准／駁回。
        ///
        /// <para>⚠️ <b>這條路徑不得進離線佇列</b>（見類別註解）。佇列的語意是
        /// 「晚一點送出，結果一樣」，簽核不是：排隊期間單據可能被撤回、被別人簽掉、
        /// 或步驟已推進，而 <c>ProcessApproval</c> 對這些一律 <c>return false</c>——
        /// 使用者會在幾小時後收到一則「簽核失敗」而完全不知道發生什麼事。</para>
        ///
        /// <para><b>只允許核准與駁回，不含退回修改。</b><c>ReturnedForRevision</c>
        /// 的用途是「請申請人改某個地方再送」，而那個「某個地方」必須寫在意見裡才有意義——
        /// 手機上打長文的成本高到會讓人只打「請修正」，那對申請人沒有任何資訊。
        /// 退回修改留在桌機。</para>
        /// </summary>
        [HttpPost("{requestId}/decide")]
        [PermissionGateExempt(
            "與桌機的 ApprovalController.Process 同一把鑰匙：授權完全由 ProcessApproval 內部的"
            + "簽核人驗證決定（非當前步驟指定的簽核人／角色一律 return false）。"
            + "在手機這一側額外加 MenuKey 閘門會做出『桌機簽得了、手機簽不了』的漂移。")]
        public async Task<IActionResult> Decide(string requestId, [FromBody] DecideRequest? req)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var action = req?.Action?.Trim().ToLowerInvariant() switch
            {
                "approve" => ApprovalStatus.Approved,
                "reject"  => ApprovalStatus.Rejected,
                _         => (ApprovalStatus?)null,
            };
            if (action == null)
                return BadRequest(new { error = "BAD_ACTION" });

            // 手機端**只有已實作摘要的單別**能簽。少了這道，前端一有 bug
            // 就會出現「看不到內容卻簽下去」——而那正是這整個設計要防的事。
            var mine = await _approvalService.GetMyPendingRequestsAsync(user.Id, user.ActiveRoleId);
            var request = mine.FirstOrDefault(r => r.Id == requestId);
            if (request == null) return NotFound(new { error = "NOT_PENDING_FOR_YOU" });
            if (!_summaries.ContainsKey(request.DocumentType))
                return BadRequest(new { error = "NOT_SUPPORTED_ON_MOBILE" });

            var ok = await _approvalService.ProcessApproval(
                requestId, user.Id, user.ActiveRoleId ?? string.Empty, action.Value, req?.Comment);

            if (!ok)
                // ProcessApproval 回 bool，分不出「已被別人簽掉」與「沒有權限」。
                // 不在這裡猜——再查一次狀態只是把競態視窗往後挪，訊息仍可能是錯的。
                // 畫面收到這個就重新載入清單，讓使用者看到現在真正的狀態。
                // `message` 是給 api.js 用的：它在非 2xx 時會 showToast(err.message)。
                // 不給的話使用者只會看到「操作失敗 (409)」，那句話沒有告訴他任何事。
                return Conflict(new
                {
                    error = "APPROVAL_FAILED",
                    message = _localizer["MobileApprovalConflict"].Value,
                });

            return Ok(new { requestId, action = action.Value.ToString() });
        }

        public sealed record DecideRequest(string? Action, string? Comment);
    }
}
