using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 生產批投產（mfg-domain.md §24.1）：查詢待投產（Planned）生產批、檢視各工單 net-at-release
    /// 重算量與料況閘口，並執行整批投產（all-or-nothing）。投產定案邏輯集中於 IWorkOrderReleaseService。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class BatchReleaseController : ModuleControllerBase
    {
        private const string MenuKey = "BatchRelease";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IWorkOrderReleaseService _release;
        private readonly IUomService _uomService;

        public BatchReleaseController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IWorkOrderReleaseService release,
            IUomService uomService)
        {
            _localizer = localizer;
            _release = release;
            _uomService = uomService;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, string sortOrder = "priority",
            string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort = sortOrder;
            ViewBag.SearchText = searchText;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<BatchReleaseRowVm>?)null);

            // 待投產 = 仍含 Planned 工單的生產批
            var plannedCounts = Db.WorkOrders.AsNoTracking()
                .Where(w => w.Status == WorkOrderStatus.Planned)
                .GroupBy(w => w.ProductionBatchId)
                .Select(g => new { BatchId = g.Key, Count = g.Count() });

            var query =
                from b in Db.ProductionBatches.AsNoTracking()
                join pc in plannedCounts on b.BatchId equals pc.BatchId
                join p in Db.Products.AsNoTracking() on b.ProductCode equals p.ProductCode into pJoin
                from p in pJoin.DefaultIfEmpty()
                where b.Status == ProductionBatchStatus.Planned
                select new BatchReleaseRowVm
                {
                    BatchId = b.BatchId,
                    ProductCode = b.ProductCode,
                    UomCode = p != null ? p.StockUom : null,
                    Quantity = b.Quantity,
                    Priority = b.Priority,
                    Status = b.Status,
                    PlannedEndDate = b.PlannedEndDate,
                    IsPredictedLate = b.IsPredictedLate,
                    PlannedWorkOrderCount = pc.Count,
                };

            if (!string.IsNullOrEmpty(searchText))
                query = query.Where(b =>
                    b.BatchId.Contains(searchText) ||
                    b.ProductCode.Contains(searchText));

            query = sortOrder switch
            {
                "batch_id"         => query.OrderBy(b => b.BatchId),
                "batch_id_desc"    => query.OrderByDescending(b => b.BatchId),
                "planned_end"      => query.OrderBy(b => b.PlannedEndDate),
                "planned_end_desc" => query.OrderByDescending(b => b.PlannedEndDate),
                "priority_desc"    => query.OrderByDescending(b => b.Priority),
                _                  => query.OrderBy(b => b.Priority)   // 優先序小者先（預設）
            };

            var list = await PaginatedList<BatchReleaseRowVm>.CreateAsync(query, pageNumber ?? 1, pageSize);
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            return View(list);
        }

        /// <summary>投產確認面板：列出批內各 Planned 工單的暫定→重算量、料況、缺料清單。</summary>
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

            var plannedWos = await Db.WorkOrders.AsNoTracking()
                .Where(w => w.ProductionBatchId == id && w.Status == WorkOrderStatus.Planned)
                .OrderBy(w => w.ParentWorkOrderId == null)
                .ThenBy(w => w.WorkOrderId)
                .Select(w => new
                {
                    w.WorkOrderId, w.ProductCode, w.ParentWorkOrderId,
                    UomCode = Db.Products.Where(p => p.ProductCode == w.ProductCode).Select(p => p.StockUom).FirstOrDefault()
                })
                .ToListAsync();

            var previews = new List<BatchReleasePreviewRowVm>();
            foreach (var w in plannedWos)
            {
                var p = await _release.PreviewAsync(w.WorkOrderId);
                previews.Add(new BatchReleasePreviewRowVm
                {
                    WorkOrderId = w.WorkOrderId,
                    ProductCode = w.ProductCode,
                    UomCode = w.UomCode,
                    IsSemi = w.ParentWorkOrderId != null,
                    PlannedQty = p.PlannedQty,
                    RecalculatedQty = p.RecalculatedQty,
                    Outcome = p.Outcome,
                    MaterialRisk = p.MaterialRisk,
                    ShortageProducts = p.ShortageProducts.ToList(),
                });
            }

            ViewBag.Previews = previews;
            ViewBag.CanRelease = await Permissions.CanUpdate(MenuKey);
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.ProductUom = await Db.Products.AsNoTracking()
                .Where(p => p.ProductCode == batch.ProductCode)
                .Select(p => p.StockUom).FirstOrDefaultAsync();
            return IsAjax ? PartialView("_ReleasePanel", batch) : View("_ReleasePanel", batch);
        }

        /// <summary>整批投產（mfg-domain.md §24.1 all-or-nothing）。委派 IWorkOrderReleaseService。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReleaseBatch(string id, bool confirmed = false)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

            var result = await _release.ReleaseBatchAsync(id, confirmed);
            return result.Outcome switch
            {
                BatchReleaseOutcome.NotFound =>
                    Json(new { success = false, message = _localizer["RecordNotFound"].Value }),
                BatchReleaseOutcome.NothingToRelease =>
                    Json(new { success = false, message = _localizer["BatchNothingToRelease"].Value }),
                BatchReleaseOutcome.MaterialBlocked =>
                    Json(new { success = false, materialBlocked = true,
                        blocked = result.Blocked.Select(b => new { workOrderId = b.WorkOrderId, shortages = b.Shortages }),
                        message = _localizer["BatchMaterialBlocked"].Value }),
                BatchReleaseOutcome.NeedsConfirm =>
                    Json(new { success = false, needsConfirm = true,
                        items = result.NeedsConfirm.Select(c => new { workOrderId = c.WorkOrderId, planned = c.Planned, recalculated = c.Recalculated }),
                        message = _localizer["BatchRecalcConfirm"].Value }),
                _ =>
                    // 與 ProductionBatchController.ReleaseBatch 同一份回應：有工單本該送審卻沒有簽核流程時（A3）
                    // 附上提醒並帶 warning 旗標。這個入口原本漏了，同一批從這裡投產就是靜默直接核准（2026-10-20 驗收 Y-8 自動化發現）。
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
