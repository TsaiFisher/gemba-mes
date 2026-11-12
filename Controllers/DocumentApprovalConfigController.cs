using Microsoft.AspNetCore.Authorization;
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
    [RequireMenuPermission("DocumentApprovalConfig")]
    public class DocumentApprovalConfigController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public DocumentApprovalConfigController(ApplicationDbContext context, IExportService exportService,
            IDateTimeService dateTime)
        {
            _context = context;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        /// <summary>
        /// 「要簽核」⇒「一定要有範本」。這個蘊含關係以前只靠前端連動維持，
        /// 後端完全沒驗——而下游一律寫成
        /// <c>IsApprovalRequired == true &amp;&amp; !IsNullOrEmpty(ApprovalTemplateId)</c>，
        /// 也就是「要簽核但沒範本」會被當成**未設線上簽核**，單據直接 Approved。
        /// 於是前端連動一失效（例如 2026-09-18 修掉的 d-none vs style.display），
        /// 症狀就不是版面問題，而是**簽核看似打開、單據全部自動放行且沒有任何訊息**。
        /// 這一道是後端的安全網：前端再怎麼壞，也存不出那種設定。
        /// </summary>
        private void ValidateTemplateRequired(DocumentApprovalConfig config)
        {
            if (config.IsApprovalRequired && string.IsNullOrWhiteSpace(config.ApprovalTemplateId))
                ModelState.AddModelError(nameof(config.ApprovalTemplateId),
                    CoreText.Get("Msg_DocApprovalTemplateRequired"));
        }

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize,
            string? sortOrder = null, bool searched = false)
        {
            ViewBag.SearchTerm = searchTerm;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.NameSort    = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.DocTypeSort = sortOrder == "doctype_asc" ? "doctype_desc" : "doctype_asc";

            if (!searched) return View((PaginatedList<DocumentApprovalConfig>?)null);

            var query = _context.DocumentApprovalConfigs
                .Include(c => c.ApprovalTemplate)
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.DocumentType.ToLower().Contains(lower) ||
                    c.DocumentTypeName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "name_asc"     => query.OrderBy(c => c.DocumentTypeName),
                "name_desc"    => query.OrderByDescending(c => c.DocumentTypeName),
                "doctype_asc"  => query.OrderBy(c => c.DocumentType),
                "doctype_desc" => query.OrderByDescending(c => c.DocumentType),
                _              => query.OrderBy(c => c.DocumentTypeName),
            };

            return View(await PaginatedList<DocumentApprovalConfig>.CreateAsync(query, pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        // ── 匯出 ─────────────────────────────────────────────────────────────────

        [HttpGet]
        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Print)]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _context.DocumentApprovalConfigs
                .Include(c => c.ApprovalTemplate)
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.DocumentType.ToLower().Contains(lower) ||
                    c.DocumentTypeName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "name_desc"    => query.OrderByDescending(c => c.DocumentTypeName),
                "doctype_asc"  => query.OrderBy(c => c.DocumentType),
                "doctype_desc" => query.OrderByDescending(c => c.DocumentType),
                _              => query.OrderBy(c => c.DocumentTypeName),
            };

            var data = await query.ToListAsync();
            var now = _dateTime.Now;

            var req = new ExportRequest
            {
                Title = isZh ? "作業類型簽核設定" : "Document Approval Config",
                Headers = isZh
                    ? ["作業名稱", "類型代碼", "需要簽核", "簽核模板"]
                    : ["Document Type Name", "Document Type", "Require Approval", "Approval Template"],
                Rows = data.Select(c => new object?[]
                {
                    c.DocumentTypeName,
                    c.DocumentType,
                    c.IsApprovalRequired ? (isZh ? "是" : "Yes") : (isZh ? "否" : "No"),
                    c.ApprovalTemplate?.Name ?? ""
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"DocumentApprovalConfig_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"DocumentApprovalConfig_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"DocumentApprovalConfig_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Create)]
        public async Task<IActionResult> Create()
        {
            await PrepareDropdowns();
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_ConfigForm", new DocumentApprovalConfig());
            return View("_ConfigForm", new DocumentApprovalConfig());
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Create)]
        public async Task<IActionResult> Create(DocumentApprovalConfig config)
        {
            ModelState.Remove(nameof(config.ApprovalTemplate));
            if (!config.IsApprovalRequired) config.ApprovalTemplateId = null;
            ValidateTemplateRequired(config);

            if (ModelState.IsValid)
            {
                _context.DocumentApprovalConfigs.Add(config);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DocApprovalConfigDuplicate") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_DocApprovalConfigCreated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            await PrepareDropdowns(config.ApprovalTemplateId);
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_ConfigForm", config);
            return View("_ConfigForm", config);
        }

        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id)
        {
            var config = await _context.DocumentApprovalConfigs.FindAsync(id);
            if (config == null) return NotFound();
            await PrepareDropdowns(config.ApprovalTemplateId);
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ConfigForm", config);
            return View("_ConfigForm", config);
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id, DocumentApprovalConfig updated)
        {
            if (id != updated.Id) return NotFound();
            ModelState.Remove(nameof(updated.ApprovalTemplate));
            if (!updated.IsApprovalRequired) updated.ApprovalTemplateId = null;
            ValidateTemplateRequired(updated);

            if (ModelState.IsValid)
            {
                var config = await _context.DocumentApprovalConfigs.FindAsync(id);
                if (config == null) return NotFound();
                config.DocumentTypeName = updated.DocumentTypeName;
                config.IsApprovalRequired = updated.IsApprovalRequired;
                config.ApprovalTemplateId = updated.ApprovalTemplateId;
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_UpdateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_DocApprovalConfigUpdated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            await PrepareDropdowns(updated.ApprovalTemplateId);
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ConfigForm", updated);
            return View("_ConfigForm", updated);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        [RequireMenuPermission("DocumentApprovalConfig", MenuPermissionAction.Delete)]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var config = await _context.DocumentApprovalConfigs.FindAsync(id);
            if (config == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_SettingNotFound") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            // ── 使用中檢查：是否有進行中的簽核流程屬於此作業類型 ──
            if (await _context.ApprovalRequests.IgnoreQueryFilters()
                    .AnyAsync(r => r.DocumentType == config.DocumentType && r.Status == ApprovalStatus.Pending))
            {
                var msg = CoreText.Get("Msg_DocApprovalConfigInUseFmt", config.DocumentTypeName);
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            _context.DocumentApprovalConfigs.Remove(config);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_SettingDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private async Task PrepareDropdowns(string? selectedTemplateId = null)
        {
            var templates = await _context.ApprovalTemplates.AsNoTracking()
                .OrderBy(t => t.Name).ToListAsync();
            ViewBag.Templates = new SelectList(templates, "Id", "Name", selectedTemplateId);
        }
    }
}
