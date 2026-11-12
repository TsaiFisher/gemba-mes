using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    public class ApprovalController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IApprovalService _approvalService;
        private readonly IPermissionService _permissionService;

        public ApprovalController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IApprovalService approvalService,
            IPermissionService permissionService)
        {
            _context = context;
            _userManager = userManager;
            _approvalService = approvalService;
            _permissionService = permissionService;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        // ── 待簽核清單 ────────────────────────────────────────────────────────────

        [PermissionGateExempt("只列出「指派給呼叫者本人」的待簽核單（GetMyPendingRequestsAsync 以 userId/roleId 過濾）")]
        public async Task<IActionResult> PendingIndex()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            // DB 端過濾（判定語意集中於 ApprovalService.MyPendingQuery，與 ProcessApproval 一致）
            var myRequests = await _approvalService.GetMyPendingRequestsAsync(user.Id, user.ActiveRoleId);

            // 附加步驟資訊至 ViewBag
            var currentStepIds = myRequests.Select(r => r.CurrentStepId).Distinct().ToList();
            ViewBag.StepsMap = await _context.ApprovalSteps
                .Where(s => currentStepIds.Contains(s.Id))
                .AsNoTracking()
                .ToDictionaryAsync(s => s.Id);

            // 申請人顯示名稱
            ViewBag.RequesterNames = await ResolveUserNamesAsync(myRequests.Select(r => r.RequesterId));

            return View(myRequests);
        }

        /// <summary>
        /// 可否檢視這張簽核申請：本人是關係人（申請人／簽核人／曾簽過），
        /// 或持有「所有簽核記錄」的讀取權（AllApprovalRequests，與 <see cref="AllRequests"/> 同一把鑰匙）。
        /// </summary>
        private async Task<bool> CanViewRequestAsync(string id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return false;
            if (await _approvalService.CanViewRequestAsync(id, user.Id, user.ActiveRoleId)) return true;
            return await _permissionService.CanRead("AllApprovalRequests");
        }

        /// <summary>將使用者 ID 集合解析為顯示名稱字典（清單「申請人」欄位用）。</summary>
        private async Task<Dictionary<string, string>> ResolveUserNamesAsync(IEnumerable<string?> userIds)
        {
            var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<string, string>();
            var users = await _userManager.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToListAsync();
            return users.ToDictionary(u => u.Id, u => u.UserName ?? u.Id);
        }

        // ── 所有簽核記錄（管理員查看所有狀態） ───────────────────────────────────

        public async Task<IActionResult> AllRequests(
            string? status, string? documentType, bool unassigned = false,
            string? sortOrder = null, int? pageNumber = null, int? pageSize = null)
        {
            // 權限走 MenuPermission（AllApprovalRequests），與全系統 IPermissionService 體系一致
            if (!await _permissionService.CanRead("AllApprovalRequests")) return Forbid();

            ViewBag.StatusFilter = status;
            ViewBag.DocTypeFilter = documentType;
            ViewBag.UnassignedFilter = unassigned;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.DocTypeSort = sortOrder == "doctype_asc" ? "doctype_desc" : "doctype_asc";
            ViewBag.StatusSort = sortOrder == "status_asc" ? "status_desc" : "status_asc";
            ViewBag.CreatedSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "created_desc" ? "created_asc" : "created_desc";

            var query = _context.ApprovalRequests.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<ApprovalStatus>(status, out var s))
                query = query.Where(r => r.Status == s);
            if (!string.IsNullOrEmpty(documentType))
                query = query.Where(r => r.DocumentType.Contains(documentType));
            // 未指派簽核人：Supervisor 型步驟解析失敗的 Pending 單，不會出現在任何人的待簽清單，需人工指派
            if (unassigned)
                query = query.Where(r => r.Status == ApprovalStatus.Pending
                    && string.IsNullOrEmpty(r.ActiveApproverId)
                    && _context.ApprovalSteps.Any(st => st.Id == r.CurrentStepId
                        && st.ApproverType == ApproverType.Supervisor));

            query = sortOrder switch
            {
                "doctype_asc"  => query.OrderBy(r => r.DocumentType).ThenByDescending(r => r.CreatedAt),
                "doctype_desc" => query.OrderByDescending(r => r.DocumentType).ThenByDescending(r => r.CreatedAt),
                "status_asc"   => query.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAt),
                "status_desc"  => query.OrderByDescending(r => r.Status).ThenByDescending(r => r.CreatedAt),
                "created_asc"  => query.OrderBy(r => r.CreatedAt),
                _              => query.OrderByDescending(r => r.CreatedAt)
            };

            int ps = pageSize ?? 20;
            var paged = await PaginatedList<ApprovalRequest>.CreateAsync(query, pageNumber ?? 1, ps);

            // 附加當前步驟資訊（Pending 單顯示步驟與未指派標記用）
            var stepIds = paged.Where(r => r.Status == ApprovalStatus.Pending)
                               .Select(r => r.CurrentStepId).ToList();
            ViewBag.StepsMap = await _context.ApprovalSteps
                .Where(s => stepIds.Contains(s.Id))
                .AsNoTracking()
                .ToDictionaryAsync(s => s.Id);

            ViewBag.RequesterNames = await ResolveUserNamesAsync(paged.Select(r => r.RequesterId));

            return View(paged);
        }

        // ── 審核操作（GET：顯示表單） ─────────────────────────────────────────────

        public async Task<IActionResult> Process(string id)
        {
            // 本 GET 原本只有 [Authorize]（＝只要有登入），任何人拿 requestId 就看得到
            // 別人的簽核單內容。POST 那一支由 ProcessApproval 內部驗簽核人，只有 GET 是破口。
            if (!await CanViewRequestAsync(id)) return Forbid();

            var request = await _context.ApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);
            if (request == null) return NotFound();
            if (request.Status != ApprovalStatus.Pending)
                return BadRequest(CoreText.Get("Msg_ApprovalAlreadyClosed"));

            var step = await _context.ApprovalSteps.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == request.CurrentStepId);

            ViewBag.Request = request;
            ViewBag.Step = step;

            if (IsAjax) return PartialView("_ProcessForm", request);
            return View("_ProcessForm", request);
        }

        // ── 審核操作（POST：送出核准/拒絕） ──────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Process(string id, ApprovalStatus approvalAction, string? comment)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var roleId = user.ActiveRoleId ?? string.Empty;
            var success = await _approvalService.ProcessApproval(id, user.Id, roleId, approvalAction, comment);

            if (!success)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_ApprovalActionFailed") });
                return RedirectToAction(nameof(PendingIndex));
            }

            var doneKey = approvalAction switch
            {
                ApprovalStatus.Approved            => "Msg_ApprovalDoneApproved",
                ApprovalStatus.ReturnedForRevision => "Msg_ApprovalDoneReturned",
                _                                  => "Msg_ApprovalDoneRejected"
            };
            if (IsAjax) return Json(new { success = true, message = CoreText.Get(doneKey) });
            return RedirectToAction(nameof(PendingIndex));
        }

        // ── 簽核歷程（查看單一 Request 的所有 Log） ───────────────────────────────

        public async Task<IActionResult> History(string id)
        {
            // 歷程含簽核人姓名與意見（comment），同樣不該讓非關係人以 requestId 直接查閱。
            if (!await CanViewRequestAsync(id)) return Forbid();

            var request = await _context.ApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);
            if (request == null) return NotFound();

            var trustedId = request.Id;
            var logs = await _context.ApprovalLogs
                .Where(l => l.RequestId == trustedId)
                .OrderBy(l => l.ActionTime)
                .AsNoTracking()
                .ToListAsync();

            ViewBag.Request = request;

            // 附加審核者名稱（先 ToList 再於記憶體投影，確保測試環境相容）；含代理簽核的被代理人
            var approverIds = logs.Select(l => l.ApproverId)
                .Concat(logs.Where(l => l.OnBehalfOfUserId != null).Select(l => l.OnBehalfOfUserId!))
                .Distinct().ToList();
            var approvers = await _userManager.Users
                .Where(u => approverIds.Contains(u.Id))
                .ToListAsync();
            ViewBag.ApproverNames = approvers.ToDictionary(a => a.Id, a => a.UserName ?? a.Id);

            if (IsAjax) return PartialView("_History", logs);
            return View("_History", logs);
        }

        // ── 撤回簽核申請 ────────────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var success = await _approvalService.WithdrawRequest(id, user.Id);
            if (IsAjax)
                return Json(new { success, message = success ? CoreText.Get("Msg_ApprovalWithdrawn") : CoreText.Get("Msg_ApprovalWithdrawFailed") });
            return RedirectToAction(nameof(PendingIndex));
        }

        // ── 重新指派簽核人（管理員） ──────────────────────────────────────────────

        public async Task<IActionResult> ReassignForm(string id)
        {
            if (!await _permissionService.CanUpdate("AllApprovalRequests")) return Forbid();

            var request = await _context.ApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);
            if (request == null) return NotFound();
            if (request.Status != ApprovalStatus.Pending)
                return BadRequest(CoreText.Get("Msg_ApprovalNotPending"));

            var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
            ViewBag.Users = new SelectList(users, "Id", "UserName", request.ActiveApproverId);
            ViewBag.CurrentActiveApproverId = request.ActiveApproverId;

            if (IsAjax) return PartialView("_ReassignForm", request);
            return View("_ReassignForm", request);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ReassignApprover(string id, string newApproverId, string? reason)
        {
            if (!await _permissionService.CanUpdate("AllApprovalRequests")) return Forbid();

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var success = await _approvalService.ReassignApprover(id, newApproverId, user.Id, reason);
            if (IsAjax)
                return Json(new { success, message = success ? CoreText.Get("Msg_ApproverReassigned") : CoreText.Get("Msg_ReassignFailed") });
            return RedirectToAction(nameof(AllRequests));
        }
    }
}
