using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    /// <summary>
    /// 合格供應商評分卡（pur-domain.md §6 Phase G+ SRM）— 全域主檔（跨廠彙總，非 IPlantEntity）。
    /// Quality/Delivery 分數由系統自動計算（存檔時後端重算為準），Price/Service 人工填寫；
    /// 核准後自動回寫 Company.QualificationStatus（Pass→Qualified / Conditional→Probation / Fail→Suspended）。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class VendorEvaluationController : ModuleControllerBase
    {
        private const string MenuKey = "VendorEvaluation";
        private const string DocType = "VendorEvaluation";

        private readonly IVendorEvaluationScoreService _score;
        private readonly IExportService   _exportService;
        private readonly IDateTimeService _dt;
        private readonly IApprovalService _approval;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public VendorEvaluationController(
            IVendorEvaluationScoreService score,
            IExportService exportService,
            IDateTimeService dt,
            IApprovalService approval,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _score         = score;
            _exportService = exportService;
            _dt            = dt;
            _approval      = approval;
            _localizer     = localizer;
        }

        // GET /VendorEvaluation
        public async Task<IActionResult> Index(
            string? searchSupplier, int? year, string? resultStatus, string quarter = "",
            bool searched = false, string sortOrder = "date_desc",
            int pageNumber = 1, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchSupplier  = searchSupplier;
            ViewBag.Year            = year;
            ViewBag.Quarter         = quarter;
            ViewBag.ResultStatus    = resultStatus;
            ViewBag.Searched        = searched;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            await PrepareSupplierOptionsAsync();

            if (!searched) return View((PaginatedList<VendorEvaluation>?)null);

            var query = BuildQuery(searchSupplier, year, quarter, resultStatus);

            query = sortOrder switch
            {
                "supplier_asc"  => query.OrderBy(v => v.SupplierCode).ThenByDescending(v => v.EvaluationYear),
                "supplier_desc" => query.OrderByDescending(v => v.SupplierCode).ThenByDescending(v => v.EvaluationYear),
                "total_asc"     => query.OrderBy(v => v.TotalScore),
                "total_desc"    => query.OrderByDescending(v => v.TotalScore),
                "date_asc"      => query.OrderBy(v => v.EvaluationYear).ThenBy(v => v.EvaluationQuarter),
                _               => query.OrderByDescending(v => v.EvaluationYear)
                                        .ThenByDescending(v => v.EvaluationQuarter)
                                        .ThenBy(v => v.SupplierCode),
            };

            // 歷年評核趨勢（§6.3）：選定單一廠商時顯示核准評核的總分折線
            if (!string.IsNullOrEmpty(searchSupplier))
            {
                var trend = await Db.VendorEvaluations.AsNoTracking()
                    .Where(v => v.SupplierCode == searchSupplier
                             && v.ApprovalStatus == DocumentApprovalStatus.Approved)
                    .OrderBy(v => v.EvaluationYear).ThenBy(v => v.EvaluationQuarter)
                    .Select(v => new { v.EvaluationYear, v.EvaluationQuarter, v.TotalScore })
                    .ToListAsync();
                ViewBag.TrendPoints = trend
                    .Select(t => new object[]
                    {
                        t.EvaluationQuarter.HasValue
                            ? $"{t.EvaluationYear} Q{t.EvaluationQuarter}"
                            : $"{t.EvaluationYear}",
                        t.TotalScore
                    })
                    .ToList();
            }

            var list = await PaginatedList<VendorEvaluation>.CreateAsync(query, pageNumber, pageSize);
            return View(list);
        }

        // GET /VendorEvaluation/Create（AJAX side panel）
        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            var today = _dt.GetLocalDate();
            var vm = new VendorEvaluation
            {
                EvaluationYear = today.Year,
                EvaluationDate = today,
                PriceScore     = 100,
                ServiceScore   = 100,
            };
            await PrepareFormViewBagAsync();
            return IsAjax ? PartialView("_Form", vm) : View("_Form", vm);
        }

        // GET /VendorEvaluation/Edit/{id}
        public async Task<IActionResult> Edit(int id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var item = await Db.VendorEvaluations.FindAsync(id);
            if (item == null) return NotFound();
            await PrepareFormViewBagAsync();
            return IsAjax ? PartialView("_Form", item) : View("_Form", item);
        }

        // GET /VendorEvaluation/Details/{id} — 待我簽核 docDetailsUrls 唯讀顯示
        public async Task<IActionResult> Details(int id)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            var item = await Db.VendorEvaluations.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id);
            if (item == null) return NotFound();
            await PrepareFormViewBagAsync();
            ViewBag.IsReadOnly = true;
            return PartialView("_Form", item);
        }

        // GET /VendorEvaluation/ComputeScores — _Form「重新計算」帶入自動分數與依據
        [HttpGet]
        public async Task<IActionResult> ComputeScores(string supplierCode, int year, int? quarter)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            if (string.IsNullOrWhiteSpace(supplierCode))
                return Json(new { success = false, message = _localizer["VendorEvalSelectSupplierFirst"].Value });

            var basis = await _score.ComputeAsync(supplierCode, year, quarter);
            var w     = await _score.GetWeightsAsync();
            return Json(new
            {
                success        = true,
                qualityScore   = basis.QualityScore  ?? 100,
                deliveryScore  = basis.DeliveryScore ?? 100,
                hasQualityData  = basis.QualityScore.HasValue,
                hasDeliveryData = basis.DeliveryScore.HasValue,
                receiptQty     = basis.ReceiptQty,
                ngQty          = basis.NgQty,
                otdTotalLines  = basis.OtdTotalLines,
                otdOnTimeLines = basis.OtdOnTimeLines,
                weights        = new { quality = w.Quality, delivery = w.Delivery, price = w.Price, service = w.Service },
            });
        }

        // POST /VendorEvaluation/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(VendorEvaluation vm)
        {
            ModelState.Remove(nameof(VendorEvaluation.Evaluator));
            ModelState.Remove(nameof(VendorEvaluation.CreatedBy));
            if (vm.EvaluationQuarter is < 1 or > 4) vm.EvaluationQuarter = null;

            if (!ModelState.IsValid) return await FormWithErrorsAsync(vm);

            // 廠商 × 年度 × 季度唯一（季度 null＝年度評核；PG 唯一索引擋不住 NULL 重複，應用層檢查）
            var dup = await Db.VendorEvaluations.AnyAsync(v =>
                v.Id != vm.Id
                && v.SupplierCode == vm.SupplierCode
                && v.EvaluationYear == vm.EvaluationYear
                && v.EvaluationQuarter == vm.EvaluationQuarter);
            if (dup)
            {
                ModelState.AddModelError(string.Empty, _localizer["VendorEvalDuplicate"].Value);
                return await FormWithErrorsAsync(vm);
            }

            // 自動分數以後端重算為準（§6.2）。
            // 無資料的項目**不計入加權**（見 VendorEvaluationCalculator.Total），
            // 欄位仍存 100 當佔位，真正的判讀依據是 QualityHasData / DeliveryHasData。
            var basis = await _score.ComputeAsync(vm.SupplierCode, vm.EvaluationYear, vm.EvaluationQuarter);
            vm.QualityScore    = basis.QualityScore  ?? 100;
            vm.DeliveryScore   = basis.DeliveryScore ?? 100;
            vm.QualityHasData  = basis.QualityScore.HasValue;
            vm.DeliveryHasData = basis.DeliveryScore.HasValue;

            var w = await _score.GetWeightsAsync();
            vm.TotalScore   = VendorEvaluationCalculator.Total(
                w, basis.QualityScore, basis.DeliveryScore, vm.PriceScore, vm.ServiceScore);
            vm.ResultStatus = VendorEvaluationCalculator.ResultOf(vm.TotalScore);

            if (vm.ResultStatus == EvaluationResult.Fail && string.IsNullOrWhiteSpace(vm.FollowUpAction))
            {
                ModelState.AddModelError(nameof(VendorEvaluation.FollowUpAction),
                    _localizer["VendorEvalFollowUpRequired"].Value);
                return await FormWithErrorsAsync(vm);
            }

            var userId = UserContext.UserId ?? "";
            if (vm.Id == 0)
            {
                if (!await Permissions.CanCreate(MenuKey)) return Forbid();
                vm.Evaluator = userId;
                vm.CreatedBy = userId;
                vm.CreatedAt = vm.UpdatedAt = DateTime.UtcNow;
                Db.VendorEvaluations.Add(vm);
            }
            else
            {
                if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
                var existing = await Db.VendorEvaluations.FindAsync(vm.Id);
                if (existing == null) return NotFound();
                if (existing.ApprovalStatus != DocumentApprovalStatus.Draft
                    && existing.ApprovalStatus != DocumentApprovalStatus.Rejected)
                {
                    ModelState.AddModelError(string.Empty, _localizer["VendorEvalNotEditable"].Value);
                    return await FormWithErrorsAsync(vm);
                }

                existing.SupplierCode      = vm.SupplierCode;
                existing.EvaluationYear    = vm.EvaluationYear;
                existing.EvaluationQuarter = vm.EvaluationQuarter;
                existing.QualityScore      = vm.QualityScore;
                existing.DeliveryScore     = vm.DeliveryScore;
                // 漏這兩行的話，重新評核後旗標會停在舊值——分數更新了、「有沒有資料」卻沒有。
                existing.QualityHasData    = vm.QualityHasData;
                existing.DeliveryHasData   = vm.DeliveryHasData;
                existing.PriceScore        = vm.PriceScore;
                existing.ServiceScore      = vm.ServiceScore;
                existing.TotalScore        = vm.TotalScore;
                existing.ResultStatus      = vm.ResultStatus;
                existing.EvaluationDate    = vm.EvaluationDate;
                existing.FollowUpAction    = vm.FollowUpAction;
                existing.Remarks           = vm.Remarks;
                existing.Evaluator         = userId;
                existing.UpdatedAt         = DateTime.UtcNow;
            }

            await Db.SaveChangesAsync();
            return Content(
                "<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>",
                "text/html");
        }

        // POST /VendorEvaluation/Submit/{id} — 送簽；無簽核流程時直接核准並回寫資格狀態。
        // 不用泛型 SubmitForApprovalAsync：主鍵為 int，且核准需連動 Company.QualificationStatus。
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(int id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var doc = await Db.VendorEvaluations.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.ApprovalStatus != DocumentApprovalStatus.Draft
                && doc.ApprovalStatus != DocumentApprovalStatus.Rejected)
                return Json(new { success = false, message = _localizer["StagingErrDraftOrReject"].Value });

            // 簽核設定必須用廠別查。CLAUDE.md〈全域主檔規則〉寫的是「PlantId 參數傳 null」，
            // 但那條在這個資料模型下永遠查不到東西：DocumentApprovalConfig 實作 IPlantEntity，
            // 存檔時 ApplyGlobalValues 會把 PlantId ??= CurrentPlantId 填上（沒有任何入口
            // 建得出「不指定廠別」的設定），而 GetApprovalConfig 沒有 IgnoreQueryFilters，
            // 全域查詢過濾 PlantId == CurrentPlantId 也還壓著——條件要 null、過濾要廠別，
            // 雙重不可能。查不到的後果不是卡住而是**放行**：走「無簽核設定」分支直接標成
            // 已核准並回寫供應商資格狀態，完全不經任何人核准且沒有訊號。
            // 其餘全域主檔（Product／Company／Tax）本來就是傳廠別的，此處對齊它們。
            var config = await _approval.GetApprovalConfig(DocType, UserContext.PlantId);
            if (config?.ApprovalTemplateId == null)
            {
                doc.ApprovalStatus = DocumentApprovalStatus.Approved;
                await ApplyResultToCompanyAsync(doc);
                await Db.SaveChangesAsync();
                return Json(new { success = true, message = _localizer["StagingApprovedNoWorkflow"].Value });
            }

            var request = await _approval.SubmitForApproval(
                doc.Id.ToString(), DocType, config.ApprovalTemplateId, UserContext.UserId ?? "");
            doc.ApprovalRequestId = request.Id;
            doc.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["StagingSubmitted"].Value });
        }

        // POST /VendorEvaluation/Withdraw/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(int id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var doc = await Db.VendorEvaluations.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.ApprovalStatus != DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrMustBePending"].Value });

            if (!await _approval.WithdrawRequest(doc.ApprovalRequestId!, UserContext.UserId ?? ""))
                return Json(new { success = false, message = _localizer["StagingWithdrawFailed"].Value });

            doc.ApprovalStatus    = DocumentApprovalStatus.Draft;
            doc.ApprovalRequestId = null;
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        // POST /VendorEvaluation/Delete/{id} — 僅 Draft/Rejected 可刪（核准後已回寫資格狀態，不可刪）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoPermission"].Value });

            var item = await Db.VendorEvaluations.FindAsync(id);
            if (item == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (item.ApprovalStatus != DocumentApprovalStatus.Draft
                && item.ApprovalStatus != DocumentApprovalStatus.Rejected)
                return Json(new { success = false, message = _localizer["VendorEvalNotEditable"].Value });

            Db.VendorEvaluations.Remove(item);
            await Db.SaveChangesAsync();
            return Json(new { success = true });
        }

        // GET /VendorEvaluation/Export — 依目前查詢條件匯出 Excel
        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchSupplier, int? year, string? resultStatus, string quarter = "")
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();

            var rows = await BuildQuery(searchSupplier, year, quarter, resultStatus)
                .OrderByDescending(v => v.EvaluationYear)
                .ThenByDescending(v => v.EvaluationQuarter)
                .ThenBy(v => v.SupplierCode)
                .ToListAsync();

            var evaluatorIds = rows.Select(r => r.Evaluator).Distinct().ToList();
            var userMap = await Db.Users.AsNoTracking()
                .Where(u => evaluatorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var req = new ExportRequest
            {
                Title   = isZh ? "供應商評分卡" : "Vendor Evaluations",
                Headers = isZh
                    ? ["廠商", "廠商名稱", "年度", "季度", "品質分數", "交期分數", "價格分數", "服務分數", "總分", "評核結果", "簽核狀態", "評核日", "評核人", "改善措施"]
                    : ["Supplier", "Name", "Year", "Quarter", "Quality", "Delivery", "Price", "Service", "Total", "Result", "Approval", "Eval Date", "Evaluator", "Follow-up"],
                Rows = rows.Select(r => new object?[]
                {
                    r.SupplierCode,
                    r.Supplier?.CompanyName,
                    r.EvaluationYear,
                    r.EvaluationQuarter.HasValue ? $"Q{r.EvaluationQuarter}" : (isZh ? "年度" : "Annual"),
                    r.QualityScore, r.DeliveryScore, r.PriceScore, r.ServiceScore, r.TotalScore,
                    r.ResultStatus.ToString(),
                    r.ApprovalStatus.ToString(),
                    r.EvaluationDate.ToString("yyyy-MM-dd"),
                    userMap.TryGetValue(r.Evaluator, out var n) ? n : r.Evaluator,
                    r.FollowUpAction,
                }).ToList()
            };

            return File(_exportService.ToExcel(req),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Uri.EscapeDataString($"VendorEvaluation_{_dt.Now:yyyyMMdd_HHmm}.xlsx"));
        }

        // GET /VendorEvaluation/Print/{id} — 評分卡列印（瀏覽器列印/另存 PDF）
        public async Task<IActionResult> Print(int id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.VendorEvaluations.AsNoTracking()
                .Include(v => v.Supplier)
                .FirstOrDefaultAsync(v => v.Id == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(
                Url.Action("Details", null, new { id }, Request.Scheme)!,
                new[] { doc.Evaluator, doc.CreatedBy });
            await PreparePrintSignAsync(id.ToString(), doc.CreatedBy);
            ViewBag.Weights = await _score.GetWeightsAsync();
            return View(doc);
        }

        // ── Private ─────────────────────────────────────────────────────────

        private IQueryable<VendorEvaluation> BuildQuery(
            string? searchSupplier, int? year, string quarter, string? resultStatus)
        {
            var query = Db.VendorEvaluations.AsNoTracking()
                .Include(v => v.Supplier)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchSupplier))
                query = query.Where(v => v.SupplierCode == searchSupplier);
            if (year.HasValue)
                query = query.Where(v => v.EvaluationYear == year.Value);
            if (quarter == "Y")
                query = query.Where(v => v.EvaluationQuarter == null);
            else if (int.TryParse(quarter, out var q) && q is >= 1 and <= 4)
                query = query.Where(v => v.EvaluationQuarter == q);
            if (Enum.TryParse<EvaluationResult>(resultStatus, out var rs))
                query = query.Where(v => v.ResultStatus == rs);

            return query;
        }

        private async Task<IActionResult> FormWithErrorsAsync(VendorEvaluation vm)
        {
            await PrepareFormViewBagAsync();
            return PartialView("_Form", vm);
        }

        private async Task PrepareSupplierOptionsAsync()
        {
            ViewBag.SupplierOptions = await Db.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();
        }

        private async Task PrepareFormViewBagAsync()
        {
            await PrepareSupplierOptionsAsync();
            ViewBag.Weights = await _score.GetWeightsAsync();
        }

        /// <summary>核准後回寫 Company.QualificationStatus（§6.1；簽核流程走 VendorEvaluationApprovalSync）。</summary>
        private async Task ApplyResultToCompanyAsync(VendorEvaluation doc)
        {
            var company = await Db.Companies
                .FirstOrDefaultAsync(c => c.CompanyCode == doc.SupplierCode);
            if (company == null) return;
            company.QualificationStatus = VendorEvaluationCalculator.StatusOf(doc.ResultStatus);
            company.UpdatedAt = DateTime.UtcNow;
        }
    }
}
