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
    // 依權限矩陣把關（原本寫死 Admin，選單看得到卻點不進去；見 AdminAccess）。類別層級＝讀取，寫入動作另標
    [RequireMenuPermission("ApprovalTemplateSetting")]
    public class ApprovalTemplateController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public ApprovalTemplateController(
            ApplicationDbContext context,
            RoleManager<ApplicationRole> roleManager,
            UserManager<ApplicationUser> userManager,
            IExportService exportService,
            IDateTimeService dateTime)
        {
            _context = context;
            _roleManager = roleManager;
            _userManager = userManager;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        // ── 清單 ─────────────────────────────────────────────────────────────────

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize,
            string? sortOrder = null, bool searched = false)
        {
            ViewBag.SearchTerm = searchTerm;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.NameSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.DocTypeSort = sortOrder == "doctype_asc" ? "doctype_desc" : "doctype_asc";

            if (!searched)
                return View((PaginatedList<ApprovalTemplate>?)null);

            var query = _context.ApprovalTemplates
                .Include(t => t.Steps)
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.Name.ToLower().Contains(lower) ||
                    (t.TargetDocumentType != null && t.TargetDocumentType.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "name_asc"     => query.OrderBy(t => t.Name),
                "name_desc"    => query.OrderByDescending(t => t.Name),
                "doctype_asc"  => query.OrderBy(t => t.TargetDocumentType),
                "doctype_desc" => query.OrderByDescending(t => t.TargetDocumentType),
                _              => query.OrderBy(t => t.Name),
            };

            return View(await PaginatedList<ApprovalTemplate>.CreateAsync(query, pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        // ── 匯出 ─────────────────────────────────────────────────────────────────

        [HttpGet]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Print)]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _context.ApprovalTemplates
                .Include(t => t.Steps)
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.Name.ToLower().Contains(lower) ||
                    (t.TargetDocumentType != null && t.TargetDocumentType.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "name_desc"    => query.OrderByDescending(t => t.Name),
                "doctype_asc"  => query.OrderBy(t => t.TargetDocumentType),
                "doctype_desc" => query.OrderByDescending(t => t.TargetDocumentType),
                _              => query.OrderBy(t => t.Name),
            };

            var data = await query.ToListAsync();
            var now = _dateTime.Now;

            var req = new ExportRequest
            {
                Title = isZh ? "簽核模板設定" : "Approval Template Settings",
                Headers = isZh
                    ? ["模板名稱", "適用單據類型", "步驟數"]
                    : ["Template Name", "Document Type", "Steps"],
                Rows = data.Select(t => new object?[]
                {
                    t.Name,
                    t.TargetDocumentType ?? "",
                    t.Steps.Count
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"ApprovalTemplate_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"ApprovalTemplate_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"ApprovalTemplate_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // ── 建立模板 ─────────────────────────────────────────────────────────────

        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Create)]
        public IActionResult Create()
        {
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_TemplateForm", new ApprovalTemplate());
            return View("_TemplateForm", new ApprovalTemplate());
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Create)]
        public async Task<IActionResult> Create(ApprovalTemplate template)
        {
            ModelState.Remove(nameof(template.Steps));
            if (ModelState.IsValid)
            {
                _context.ApprovalTemplates.Add(template);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CreateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_TemplateCreated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_TemplateForm", template);
            return View("_TemplateForm", template);
        }

        // ── 編輯模板 ─────────────────────────────────────────────────────────────

        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id)
        {
            var template = await _context.ApprovalTemplates.FindAsync(id);
            if (template == null) return NotFound();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_TemplateForm", template);
            return View("_TemplateForm", template);
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id, ApprovalTemplate updated)
        {
            if (id != updated.Id) return NotFound();
            ModelState.Remove(nameof(updated.Steps));
            if (ModelState.IsValid)
            {
                var template = await _context.ApprovalTemplates.FindAsync(id);
                if (template == null) return NotFound();
                template.Name = updated.Name;
                template.TargetDocumentType = updated.TargetDocumentType;
                template.SkipSameApprover = updated.SkipSameApprover;
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_UpdateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_TemplateUpdated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_TemplateForm", updated);
            return View("_TemplateForm", updated);
        }

        // ── 刪除模板 ─────────────────────────────────────────────────────────────

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Delete)]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var template = await _context.ApprovalTemplates
                .Include(t => t.Steps)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (template == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_TemplateNotFound") });
                return NotFound();
            }

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            if (await _context.DocumentApprovalConfigs.AnyAsync(c => c.ApprovalTemplateId == id))
                reasons.Add(CoreText.Get("Msg_InUse_DocApprovalConfig"));

            if (await TemplateHasPendingRequestsAsync(id))
                reasons.Add(CoreText.Get("Msg_InUse_PendingApprovals"));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_TemplateInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            _context.ApprovalSteps.RemoveRange(template.Steps);
            _context.ApprovalTemplates.Remove(template);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_TemplateDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // ── 模板明細（步驟管理） ──────────────────────────────────────────────────

        public async Task<IActionResult> Details(string id)
        {
            var template = await _context.ApprovalTemplates
                .Include(t => t.Steps)
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);
            if (template == null) return NotFound();

            await PrepareStepDropdowns();
            ViewBag.TemplateId = id;
            if (IsAjax) return PartialView("_TemplateDetails", template);
            return View("_TemplateDetails", template);
        }

        // ── 新增步驟 ─────────────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> AddStep(ApprovalStep step)
        {
            ModelState.Remove(nameof(step.Template));
            var template = await _context.ApprovalTemplates.FindAsync(step.TemplateId);
            if (template == null) return Json(new { success = false, message = CoreText.Get("Msg_TemplateNotFound") });
            var trustedTemplateId = template.Id;
            step.TemplateId = trustedTemplateId;

            if (await TemplateHasPendingRequestsAsync(trustedTemplateId))
                return Json(new { success = false, message = StepLockedMessage });

            if (ModelState.IsValid)
            {
                // 自動決定 Sequence（最大 + 1）
                var maxSeq = await _context.ApprovalSteps
                    .Where(s => s.TemplateId == trustedTemplateId)
                    .Select(s => (int?)s.Sequence)
                    .MaxAsync() ?? 0;
                step.Sequence = maxSeq + 1;
                _context.ApprovalSteps.Add(step);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    return Json(new { success = false, message = CoreText.Get("Msg_CreateFailed") });
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_StepAdded") });
            }
            if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_StepInvalid") });
            return RedirectToAction(nameof(Details), new { id = step.TemplateId });
        }

        // ── 刪除步驟 ─────────────────────────────────────────────────────────────

        [HttpPost, ActionName("DeleteStep"), ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> DeleteStepConfirmed(string id, string templateId)
        {
            var step = await _context.ApprovalSteps.FindAsync(id);
            if (step != null)
            {
                var trustedTemplateId = step.TemplateId;

                // 進行中簽核守門：刪除 in-flight 申請單指向的步驟會讓該單永遠卡死
                if (trustedTemplateId != null && await TemplateHasPendingRequestsAsync(trustedTemplateId))
                    return Json(new { success = false, message = StepLockedMessage });

                // 歷史簽核紀錄引用（CurrentStepId FK Restrict）：先給明確訊息，避免落到 DB 例外的通用訊息
                if (await _context.ApprovalRequests.IgnoreQueryFilters().AnyAsync(r => r.CurrentStepId == id))
                    return Json(new { success = false, message = CoreText.Get("Msg_StepInUse") });

                _context.ApprovalSteps.Remove(step);
                try
                {
                    await _context.SaveChangesAsync();

                    // 重新排列剩餘步驟的 Sequence
                    var remaining = await _context.ApprovalSteps
                        .Where(s => s.TemplateId == trustedTemplateId)
                        .OrderBy(s => s.Sequence)
                        .ToListAsync();
                    for (int i = 0; i < remaining.Count; i++)
                        remaining[i].Sequence = i + 1;
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                    return StatusCode(500);
                }
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_StepDeleted") });
            return RedirectToAction(nameof(Details), new { id = templateId });
        }

        // ── 修改步驟 ─────────────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> EditStep(string id, string templateId, int approverType,
            string? targetRoleId, string? targetUserId)
        {
            var step = await _context.ApprovalSteps.FindAsync(id);
            if (step == null) return Json(new { success = false, message = CoreText.Get("Msg_StepNotFound") });

            var template = await _context.ApprovalTemplates.FindAsync(templateId);
            if (template == null || step.TemplateId != template.Id)
                return Json(new { success = false, message = CoreText.Get("Msg_TemplateNotFound") });

            if (await TemplateHasPendingRequestsAsync(template.Id))
                return Json(new { success = false, message = StepLockedMessage });

            step.ApproverType = (ApproverType)approverType;
            step.TargetRoleId = step.ApproverType == ApproverType.Role ? targetRoleId : null;
            step.TargetUserId = step.ApproverType == ApproverType.User ? targetUserId : null;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = CoreText.Get("Msg_UpdateFailed") });
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_StepUpdated") });
            return RedirectToAction(nameof(Details), new { id = templateId });
        }

        // ── 步驟向上/向下移動 ─────────────────────────────────────────────────────

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("ApprovalTemplateSetting", MenuPermissionAction.Update)]
        public async Task<IActionResult> MoveStep(string id, string templateId, string direction)
        {
            var template = await _context.ApprovalTemplates.FindAsync(templateId);
            if (template == null) return Json(new { success = false });
            var trustedTemplateId = template.Id;

            if (await TemplateHasPendingRequestsAsync(trustedTemplateId))
                return Json(new { success = false, message = StepLockedMessage });

            var steps = await _context.ApprovalSteps
                .Where(s => s.TemplateId == trustedTemplateId)
                .OrderBy(s => s.Sequence)
                .ToListAsync();

            var idx = steps.FindIndex(s => s.Id == id);
            if (idx < 0) return Json(new { success = false });

            int swapIdx = direction == "up" ? idx - 1 : idx + 1;
            if (swapIdx < 0 || swapIdx >= steps.Count)
                return Json(new { success = false });

            (steps[idx].Sequence, steps[swapIdx].Sequence) = (steps[swapIdx].Sequence, steps[idx].Sequence);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Json(new { success = false, message = CoreText.Get("Msg_MoveFailed") });
            }
            return Json(new { success = true });
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static string StepLockedMessage => CoreText.Get("Msg_TemplateStepsLocked");

        /// <summary>
        /// 模板底下是否有 Pending 申請單停在任一步驟（IgnoreQueryFilters：跨廠、跨層級檢查）。
        /// 步驟的新增/刪除/修改/搬移都會改變 in-flight 申請單的路徑或簽核人，一律先擋下。
        /// </summary>
        private async Task<bool> TemplateHasPendingRequestsAsync(string templateId)
        {
            var stepIds = _context.ApprovalSteps.Where(s => s.TemplateId == templateId).Select(s => s.Id);
            return await _context.ApprovalRequests.IgnoreQueryFilters()
                .AnyAsync(r => stepIds.Contains(r.CurrentStepId) && r.Status == ApprovalStatus.Pending);
        }

        private async Task PrepareStepDropdowns()
        {
            var roles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
            var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
            ViewBag.Roles = new SelectList(roles, "Id", "Name");
            ViewBag.Users = new SelectList(users, "Id", "UserName");
        }
    }
}
