using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// MRP 採購對帳差異決策清單（mfg-domain.md §10-5 P2）。
    ///
    /// 重跑後「本次已無需求、但既有請購單仍留著」這類落差，過去只以一次性警示呈現，
    /// 使用者關掉視窗就消失。本清單把差異項變成必須被結清的待辦，持續呈現直到處理完畢。
    ///
    /// 唯讀工作清單 + 兩個結清動作：可安全退場者一鍵結案（伺服器端重新驗證，不信任前端）、
    /// 其餘由人工在各自單據處理後標記為已處理。
    /// </summary>
    [Authorize]
    public class MrpVarianceDecisionController : ModuleControllerBase
    {
        private const string MenuKey = "MrpVarianceDecision";

        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IUomService _uomService;

        public MrpVarianceDecisionController(
            IStringLocalizer<ProjectZSharedResource> localizer, IUomService uomService)
        {
            _localizer = localizer;
            _uomService = uomService;
        }

        public async Task<IActionResult> Index(
            string? searchProduct, string? searchMrpRunId, bool onlyOpen = true,
            string? sortOrder = null, bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.DetectedSort    = sortOrder == "detected_asc" ? "detected_desc" : "detected_asc";
            ViewBag.SearchProduct   = searchProduct;
            ViewBag.SearchMrpRunId  = searchMrpRunId;
            ViewBag.OnlyOpen        = onlyOpen;
            ViewBag.UomDecimals     = await _uomService.GetAllDecimalPlacesAsync();
            await PrepareIndexPermissionsAsync(MenuKey);

            ViewBag.ProductOptions = await Db.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName }).ToListAsync();
            ViewBag.MrpRunOptions = await Db.MrpPurchaseVarianceRecords
                .AsNoTracking().Select(v => v.MrpRunId).Distinct().OrderByDescending(x => x)
                .Take(200).ToListAsync();

            if (!searched) return View((PaginatedList<MrpVarianceDecisionRow>?)null);

            var query = from v in Db.MrpPurchaseVarianceRecords.AsNoTracking()
                        join p in Db.Products.AsNoTracking()
                            on v.ProductCode equals p.ProductCode into pJoin
                        from p in pJoin.DefaultIfEmpty()
                        select new MrpVarianceDecisionRow
                        {
                            VarianceId        = v.VarianceId,
                            MrpRunId          = v.MrpRunId,
                            ProductCode       = v.ProductCode,
                            UomCode           = p != null ? p.StockUom : null,
                            PurchaseRequestId = v.PurchaseRequestId,
                            LineNo            = v.LineNo,
                            Kind              = v.Kind,
                            Disposition       = v.Disposition,
                            Status            = v.Status,
                            PreviousQty       = v.PreviousQty,
                            CurrentQty        = v.CurrentQty,
                            Description       = v.Description,
                            DetectedAt        = v.DetectedAt,
                            ResolvedAt        = v.ResolvedAt
                        };

            if (onlyOpen)
                query = query.Where(r => r.Status == MrpVarianceStatus.Open);
            if (!string.IsNullOrWhiteSpace(searchProduct))
                query = query.Where(r => r.ProductCode == searchProduct);
            if (!string.IsNullOrWhiteSpace(searchMrpRunId))
                query = query.Where(r => r.MrpRunId == searchMrpRunId);

            query = sortOrder switch
            {
                "detected_asc"  => query.OrderBy(r => r.DetectedAt).ThenBy(r => r.ProductCode),
                "detected_desc" => query.OrderByDescending(r => r.DetectedAt).ThenBy(r => r.ProductCode),
                _               => query.OrderBy(r => r.Status)
                                        .ThenByDescending(r => r.DetectedAt).ThenBy(r => r.ProductCode),
            };

            var paged = await PaginatedList<MrpVarianceDecisionRow>.CreateAsync(
                query, pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        /// <summary>
        /// 一鍵結案退場：把差異項對應的請購明細標記 <c>IsClosed</c>。
        ///
        /// **伺服器端一律重新驗證，不信任前端送來的 id**——差異項是重跑當下的快照，
        /// 期間該明細可能已被轉採購或已由他人處理。逐項重查請購明細現況，
        /// 只有「仍未轉採購、尚未結案、且單據非簽核中」者才實際結案。
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseLines(string[] varianceIds)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            if (varianceIds is not { Length: > 0 })
                return Json(new { success = false, message = _localizer["NoRowsSelected"].Value });

            var records = await Db.MrpPurchaseVarianceRecords
                .Where(v => varianceIds.Contains(v.VarianceId) && v.Status == MrpVarianceStatus.Open)
                .ToListAsync();

            var closed = 0;
            var skipped = new List<string>();
            var userId = UserContext.UserId;

            foreach (var rec in records)
            {
                // 只有「本次已無需求」的兩類才是退場對象；數量變動／新增需求不能用結案處理
                if (rec.Kind is not (MrpPurchaseVarianceKind.ModeChanged or MrpPurchaseVarianceKind.Removed))
                {
                    skipped.Add($"{rec.ProductCode}（{_localizer["VarianceNotClosable"].Value}）");
                    continue;
                }

                var line = await Db.PurchaseRequestLines
                    .Include(l => l.PurchaseRequest)
                    .FirstOrDefaultAsync(l => l.PurchaseRequestId == rec.PurchaseRequestId
                                           && l.LineNo == rec.LineNo);
                if (line == null)
                {
                    skipped.Add($"{rec.ProductCode}（{_localizer["VarianceLineMissing"].Value}）");
                    continue;
                }
                if (line.OrderedQty > 0)
                {
                    skipped.Add($"{rec.ProductCode}（{_localizer["VarianceAlreadyOrdered"].Value}）");
                    continue;
                }
                if (line.PurchaseRequest?.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                {
                    skipped.Add($"{rec.ProductCode}（{_localizer["VariancePendingApproval"].Value}）");
                    continue;
                }

                if (!line.IsClosed) line.IsClosed = true;
                MarkResolved(rec, userId, _localizer["VarianceResolvedByClose"].Value);
                closed++;
            }

            await Db.SaveChangesAsync();

            var message = _localizer["VarianceCloseResult", closed, skipped.Count].Value;
            if (skipped.Count > 0) message += "\n" + string.Join("\n", skipped);
            return Json(new { success = true, message });
        }

        /// <summary>人工在各自單據處理完畢後，把差異項標記為已結清（不動任何單據）。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Acknowledge(string[] varianceIds, string? note)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            if (varianceIds is not { Length: > 0 })
                return Json(new { success = false, message = _localizer["NoRowsSelected"].Value });

            var records = await Db.MrpPurchaseVarianceRecords
                .Where(v => varianceIds.Contains(v.VarianceId) && v.Status == MrpVarianceStatus.Open)
                .ToListAsync();

            var userId = UserContext.UserId;
            foreach (var rec in records)
                MarkResolved(rec, userId,
                    string.IsNullOrWhiteSpace(note) ? _localizer["VarianceResolvedManually"].Value : note!);

            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["VarianceAckResult", records.Count].Value });
        }

        private static void MarkResolved(MrpPurchaseVarianceRecord rec, string? userId, string note)
        {
            rec.Status = MrpVarianceStatus.Resolved;
            rec.ResolvedBy = userId;
            rec.ResolvedAt = DateTime.UtcNow;
            rec.ResolutionNote = note.Length > 500 ? note[..500] : note;
        }
    }

    /// <summary>唯讀顯示投影列。非表單綁定欄位，值型別以 [Display] 標註排除於 R5b 之外。</summary>
    public class MrpVarianceDecisionRow
    {
        public string VarianceId        { get; set; } = "";
        public string MrpRunId          { get; set; } = "";
        public string ProductCode       { get; set; } = "";
        public string? UomCode          { get; set; }
        public string PurchaseRequestId { get; set; } = "";
        public string Description       { get; set; } = "";

        [Display(Name = "Variance Line No")]
        public int LineNo { get; set; }

        [Display(Name = "Variance Kind")]
        public MrpPurchaseVarianceKind Kind { get; set; }

        [Display(Name = "Variance Disposition")]
        public MrpPurchaseVarianceDisposition Disposition { get; set; }

        [Display(Name = "Variance Status")]
        public MrpVarianceStatus Status { get; set; }

        [Display(Name = "Variance Previous Qty")]
        public decimal PreviousQty { get; set; }

        [Display(Name = "Variance Current Qty")]
        public decimal CurrentQty { get; set; }

        [Display(Name = "Variance Detected At")]
        public DateTime DetectedAt { get; set; }

        public DateTime? ResolvedAt { get; set; }
    }
}
