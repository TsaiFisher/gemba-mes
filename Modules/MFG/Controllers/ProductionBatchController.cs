using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 生產批查詢（mfg-domain.md §14.3）：一個生產批對應一筆 MRP 需求列，下含多張工單。
    /// 提供查詢、明細（含子工單進度）與優先序調整（插單往前排，§10-8）。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class ProductionBatchController : ModuleControllerBase
    {
        private const string MenuKey = "ProductionBatch";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Services.IWorkOrderReleaseService _release;
        private readonly IUomService _uomService;

        public ProductionBatchController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            Services.IWorkOrderReleaseService release,
            IUomService uomService)
        {
            _localizer = localizer;
            _release = release;
            _uomService = uomService;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "priority",
            string searchText = "", string statusFilter = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.StatusFilter = statusFilter;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.ProductUomMap = await Db.Products.AsNoTracking()
                .Select(p => new { p.ProductCode, p.StockUom }).ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);
            if (!searched) return View((PaginatedList<ProductionBatch>?)null);

            IQueryable<ProductionBatch> query = Db.ProductionBatches.AsNoTracking();

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(b =>
                    b.BatchId.Contains(searchText) ||
                    b.ProductCode.Contains(searchText) ||
                    b.MrpRunId.Contains(searchText));

            if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<ProductionBatchStatus>(statusFilter, out var st))
                query = query.Where(b => b.Status == st);

            query = sortOrder switch
            {
                "batch_id"        => query.OrderBy(b => b.BatchId),
                "batch_id_desc"   => query.OrderByDescending(b => b.BatchId),
                "planned_end"     => query.OrderBy(b => b.PlannedEndDate),
                "planned_end_desc"=> query.OrderByDescending(b => b.PlannedEndDate),
                "priority_desc"   => query.OrderByDescending(b => b.Priority),
                _                 => query.OrderBy(b => b.Priority)   // 優先序小者先（預設）
            };

            var list = await PaginatedList<ProductionBatch>.CreateAsync(query, pageNumber ?? 1, pageSize);
            return View(list);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var batch = await Db.ProductionBatches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.BatchId == id);

            if (batch == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            ViewBag.WorkOrders = await Db.WorkOrders.AsNoTracking()
                .Where(w => w.ProductionBatchId == id)
                .OrderBy(w => w.WorkOrderId)
                .Select(w => new
                {
                    w.WorkOrderId, w.ProductCode, w.Quantity, w.Status, w.ParentWorkOrderId, w.IsOutsource,
                    UomCode = Db.Products.Where(p => p.ProductCode == w.ProductCode).Select(p => p.StockUom).FirstOrDefault()
                })
                .ToListAsync();

            ViewBag.ProductUom = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == batch.ProductCode)
                .Select(p => p.StockUom).FirstOrDefaultAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            ViewBag.IsReadOnly = true;
            await PrepareIndexPermissionsAsync(MenuKey);
            return IsAjax ? PartialView("_Details", batch) : View("_Details", batch);
        }

        /// <summary>調整生產批優先序（插單往前排，mfg-domain.md §10-8）。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePriority(string id, int priority)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            // 範圍守門：對齊 UI 契約（1..9999）。前端僅 min/max 提示，server 須自行驗證，
            // 否則可直接 POST priority=-999 強行插到所有生產批之前，繞過插單區間。
            if (priority < 1 || priority > 9999)
                return Json(new { success = false, message = _localizer["PriorityOutOfRange"].Value });

            var batch = await Db.ProductionBatches.FirstOrDefaultAsync(b => b.BatchId == id);
            if (batch == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            batch.Priority = priority;
            batch.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();

            return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
        }

        /// <summary>
        /// 整批投產（mfg-domain.md §24.1 all-or-nothing）：批內所有 Planned 工單一起投產。
        /// 先逐張試算（PreviewAsync）：任一缺料（硬料閘擋住）→ 整批不投；任一重算量變動且未確認
        /// → 回傳需確認清單；全數通過才逐張定案（ApplyAsync），半成品先於成品。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReleaseBatch(string id, bool confirmed = false)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var result = await _release.ReleaseBatchAsync(id, confirmed);
            return result.Outcome switch
            {
                Services.BatchReleaseOutcome.NotFound =>
                    Json(new { success = false, message = _localizer["RecordNotFound"].Value }),
                Services.BatchReleaseOutcome.NothingToRelease =>
                    Json(new { success = false, message = _localizer["BatchNothingToRelease"].Value }),
                Services.BatchReleaseOutcome.MaterialBlocked =>
                    Json(new { success = false, materialBlocked = true,
                        blocked = result.Blocked.Select(b => new { workOrderId = b.WorkOrderId, shortages = b.Shortages }),
                        message = _localizer["BatchMaterialBlocked"].Value }),
                Services.BatchReleaseOutcome.NeedsConfirm =>
                    Json(new { success = false, needsConfirm = true,
                        items = result.NeedsConfirm.Select(c => new { workOrderId = c.WorkOrderId, planned = c.Planned, recalculated = c.Recalculated }),
                        message = _localizer["BatchRecalcConfirm"].Value }),
                _ =>
                    Json(new { success = true, released = result.Released, submitted = result.Submitted,
                        skipped = result.Skipped,
                        warning = result.UnapprovedByFlow.Count > 0,
                        message = (result.Skipped.Count == 0
                            ? _localizer["BatchReleaseSuccess"].Value
                            : _localizer["BatchReleaseSuccess"].Value + " " +
                              string.Format(_localizer["BatchReleaseSkippedFmt"].Value, string.Join("、", result.Skipped)))
                          + (result.UnapprovedByFlow.Count == 0 ? ""
                            : " " + SmartFactory.MES.Modules._Shared.PzText.Get("Mfg_ReleaseNoApprovalFlowFmt",
                                string.Join("、", result.UnapprovedByFlow))) }),
            };
        }
    }
}
