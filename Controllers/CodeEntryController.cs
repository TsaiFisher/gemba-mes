using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    // 依權限矩陣把關（原本寫死 Admin，選單看得到卻點不進去；見 AdminAccess）。類別層級＝讀取，寫入動作另標
    [RequireMenuPermission("CodeEntry")]
    public class CodeEntryController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;
        private readonly ICodeEntryReferenceChecker _referenceChecker;

        public CodeEntryController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService,
            IDateTimeService dateTime,
            ICodeEntryReferenceChecker referenceChecker)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _dateTime = dateTime;
            _referenceChecker = referenceChecker;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        public async Task<IActionResult> Index(
            string? category, string? searchTerm,
            int? pageNumber, int? pageSize,
            string? sortOrder,
            bool searched = false)
        {
            ViewBag.Category    = category;
            ViewBag.SearchTerm  = searchTerm;
            ViewBag.Searched    = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CategorySort = string.IsNullOrEmpty(sortOrder) || sortOrder == "category_asc" ? "category_desc" : "category_asc";
            ViewBag.CodeSort     = sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameZhSort   = sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.SortOrderSort = sortOrder == "sort_asc" ? "sort_desc" : "sort_asc";

            ViewBag.Categories = await _context.CodeEntries
                .AsNoTracking()
                .Select(c => c.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();

            if (!searched) return View((PaginatedList<CodeEntry>?)null);

            var query = _context.CodeEntries.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(category))
                query = query.Where(c => c.Category == category);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.Code.ToLower().Contains(lower) ||
                    c.NameZh.ToLower().Contains(lower) ||
                    (c.NameEn != null && c.NameEn.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "category_asc"  => query.OrderBy(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
                "category_desc" => query.OrderByDescending(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
                "code_asc"      => query.OrderBy(c => c.Code),
                "code_desc"     => query.OrderByDescending(c => c.Code),
                "name_asc"      => query.OrderBy(c => c.NameZh),
                "name_desc"     => query.OrderByDescending(c => c.NameZh),
                "sort_asc"      => query.OrderBy(c => c.SortOrder).ThenBy(c => c.Code),
                "sort_desc"     => query.OrderByDescending(c => c.SortOrder).ThenBy(c => c.Code),
                _               => query.OrderBy(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
            };

            return View(await PaginatedList<CodeEntry>.CreateAsync(query, pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        // ── 匯出 ─────────────────────────────────────────────────────────────────

        [HttpGet]
        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Print)]
        public async Task<IActionResult> Export(string? category, string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _context.CodeEntries.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(category))
                query = query.Where(c => c.Category == category);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.Code.ToLower().Contains(lower) ||
                    c.NameZh.ToLower().Contains(lower) ||
                    (c.NameEn != null && c.NameEn.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "category_asc"  => query.OrderBy(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
                "category_desc" => query.OrderByDescending(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
                "code_asc"      => query.OrderBy(c => c.Code),
                "code_desc"     => query.OrderByDescending(c => c.Code),
                "name_asc"      => query.OrderBy(c => c.NameZh),
                "name_desc"     => query.OrderByDescending(c => c.NameZh),
                "sort_asc"      => query.OrderBy(c => c.SortOrder).ThenBy(c => c.Code),
                "sort_desc"     => query.OrderByDescending(c => c.SortOrder).ThenBy(c => c.Code),
                _               => query.OrderBy(c => c.Category).ThenBy(c => c.SortOrder).ThenBy(c => c.Code),
            };

            var data = await query.ToListAsync();
            var now = _dateTime.Now;

            var req = new ExportRequest
            {
                Title = isZh ? "代碼維護" : "Code Entry",
                Headers = isZh
                    ? ["類別", "代碼", "中文名稱", "英文名稱", "排序", "啟用"]
                    : ["Category", "Code", "Name (ZH)", "Name (EN)", "Sort Order", "Active"],
                Rows = data.Select(c => new object?[]
                {
                    c.Category,
                    c.Code,
                    c.NameZh,
                    c.NameEn ?? "",
                    c.SortOrder,
                    c.IsActive ? (isZh ? "啟用" : "Yes") : (isZh ? "停用" : "No")
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"CodeEntry_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"CodeEntry_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"CodeEntry_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Create)]
        public IActionResult Create(string? category)
        {
            ViewBag.IsEdit = false;
            ViewBag.DefaultCategory = category;
            var model = new CodeEntry { Category = category ?? string.Empty, IsActive = true };
            if (IsAjax) return PartialView("_CodeEntryForm", model);
            return View("_CodeEntryForm", model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Create)]
        public async Task<IActionResult> Create(CodeEntry entry)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                ViewBag.DefaultCategory = entry.Category;
                if (IsAjax) return PartialView("_CodeEntryForm", entry);
                return View("_CodeEntryForm", entry);
            }

            // 簽核設定必須用廠別查。本檔原本傳 null，照的是 CLAUDE.md〈全域主檔規則〉那條
            // 「`GetApprovalConfig` 的 PlantId 參數傳 `null`（全域審核設定）」——但那條在這個
            // 資料模型下永遠查不到東西：DocumentApprovalConfig 實作 IPlantEntity，存檔時
            // ApplyGlobalValues 會把 PlantId ??= CurrentPlantId 填上（沒有任何入口建得出
            // 「不指定廠別」的設定），而 GetApprovalConfig 沒有 IgnoreQueryFilters，全域查詢
            // 過濾 PlantId == CurrentPlantId 也還壓著——條件要 null、過濾要廠別，雙重不可能。
            //
            // 查不到的後果不是卡住而是**放行**：走下面的 else 分支直接標成 Approved 並生效，
            // 為代碼維護設定的簽核流程存得下去卻永不作用，且沒有任何訊號。卡住看得出來，
            // 放行看不出來——後者嚴重得多。
            //
            // 修法對齊其餘全域主檔（Product／Company／Tax 本來就是傳廠別的）與 2026-09-01
            // 已修好的同源缺陷 VendorEvaluationController。另一個方向（GetApprovalConfig 改成
            // 先找廠別再退回全域、並讓設定畫面建得出全域列）屬設計變更而非修 bug，未採用。
            var config = await _approvalService.GetApprovalConfig("CodeEntry", _userContext.PlantId);
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                // 需要簽核：先儲存為待審核，IsActive=false
                entry.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                entry.IsActive = false;
                _context.CodeEntries.Add(entry);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CodeEntryCreateDuplicate") });
                    return StatusCode(500);
                }
                var userId = _userContext.UserId ?? string.Empty;
                var docId = $"{entry.Category}/{entry.Code}";
                var request = await _approvalService.SubmitForApproval(docId, "CodeEntry", config.ApprovalTemplateId, userId);
                entry.ApprovalRequestId = request.Id;
                await _context.SaveChangesAsync();

                // 模板開啟「同一簽核人自動跳簽」且全步驟被跳過時，送審即終局核准（狀態由中央同步回寫）
                if (IsAjax) return Json(new { success = true, message = request.Status == ApprovalStatus.Approved
                    ? CoreText.Get("Msg_CodeEntryCreatedAutoApproved") : CoreText.Get("Msg_CodeEntryCreatedSubmitted") });
            }
            else
            {
                // 無簽核設定：直接生效
                entry.ApprovalStatus = DocumentApprovalStatus.Approved;
                _context.CodeEntries.Add(entry);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CodeEntryCreateDuplicate") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_CodeEntryCreated") });
            }
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id)
        {
            var entry = await _context.CodeEntries.FindAsync(id);
            if (entry == null) return NotFound();
            // 待審核中不可編輯
            if (entry.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CodeEntryPendingApproval") });
                return BadRequest();
            }
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_CodeEntryForm", entry);
            return View("_CodeEntryForm", entry);
        }

        [HttpPost, ValidateAntiForgeryToken]
        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Update)]
        public async Task<IActionResult> Edit(string id, CodeEntry updated)
        {
            if (id != updated.Id) return NotFound();

            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_CodeEntryForm", updated);
                return View("_CodeEntryForm", updated);
            }

            var entry = await _context.CodeEntries.FindAsync(id);
            if (entry == null) return NotFound();
            if (entry.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CodeEntryPendingApproval") });
                return BadRequest();
            }

            entry.NameZh    = updated.NameZh;
            entry.NameEn    = updated.NameEn;
            entry.Remark    = updated.Remark;
            entry.SortOrder = updated.SortOrder;

            // 與 Create 同一個修正：傳廠別而不是 null，否則設定查不到會直接放行。理由見該處。
            var config = await _approvalService.GetApprovalConfig("CodeEntry", _userContext.PlantId);
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                // 修改需重新送審
                entry.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                entry.IsActive = false;
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_UpdateFailed") });
                    return StatusCode(500);
                }
                var userId = _userContext.UserId ?? string.Empty;
                var docId = $"{entry.Category}/{entry.Code}";
                var request = await _approvalService.SubmitForApproval(docId, "CodeEntry", config.ApprovalTemplateId, userId);
                entry.ApprovalRequestId = request.Id;
                await _context.SaveChangesAsync();

                if (IsAjax) return Json(new { success = true, message = request.Status == ApprovalStatus.Approved
                    ? CoreText.Get("Msg_CodeEntryUpdatedAutoApproved") : CoreText.Get("Msg_CodeEntryUpdatedSubmitted") });
            }
            else
            {
                entry.IsActive = updated.IsActive;
                entry.ApprovalStatus = DocumentApprovalStatus.Approved;
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_UpdateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_CodeEntryUpdated") });
            }
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /CodeEntry/Details/{category}/{code}  — 唯讀檢視（供簽核頁面 Side Panel 使用）
        [HttpGet, Route("CodeEntry/Details/{category}/{code}")]
        public async Task<IActionResult> Details(string category, string code)
        {
            var entry = await _context.CodeEntries
                .FirstOrDefaultAsync(e => e.Category == category && e.Code == code);
            if (entry == null) return NotFound();
            ViewBag.IsEdit = true;
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_CodeEntryForm", entry);
            return View("_CodeEntryForm", entry);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        [RequireMenuPermission("CodeEntry", MenuPermissionAction.Delete)]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var entry = await _context.CodeEntries.FindAsync(id);
            if (entry == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CodeEntryNotFound") });
                return NotFound();
            }

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            if (entry.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                reasons.Add(CoreText.Get("Msg_CodeEntryInUse_Pending"));

            // 檢查是否被其他模組資料引用（由 ICodeEntryReferenceChecker 實作）
            reasons.AddRange(await _referenceChecker.CheckReferencesAsync(entry));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_CodeEntryInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            _context.CodeEntries.Remove(entry);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_CodeEntryDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }
    }
}
