using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>重工發退料單 (RI/RR)。</summary>
    [Authorize]
    public class ReworkMaterialController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IPlantParamService _plantParams;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ReworkMaterial";
        private const string DocType = "ReworkMaterial";

        private static readonly HashSet<string> ValidTypeCodes = new() { "RI", "RR" };

        public ReworkMaterialController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IPlantParamService plantParams,
            IDocNumberService docNumberService, IInventoryPostingService postingService,
            IUomService uomService, ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _plantParams = plantParams;
            _docNumberService = docNumberService; _postingService = postingService;
            _uomService = uomService; _currencyService = currencyService;
        
            _localizer = localizer;
        }


        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom }).ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            ViewBag.WorkCenterOptions = await _context.WorkCenters
                .AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WorkCenterCode)
                .Select(w => new { w.WorkCenterCode, w.WorkCenterName }).ToListAsync();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.TransactionTypes = await _context.InventoryTransactionTypes
                .AsNoTracking().Where(t => t.IsActive)
                .Select(t => new { t.TypeCode, t.SourceTypeCode })
                .ToListAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, string? typeCode, DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm; ViewBag.TypeCodeFilter = typeCode;
            ViewBag.DateFrom = dateFrom; ViewBag.DateTo = dateTo;
            ViewBag.Searched = searched; ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<ReworkMaterialDoc>?)null);

            var query = _context.ReworkMaterialDocs.AsNoTracking().Include(d => d.Lines).AsQueryable();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.ReworkMaterialDocId.Contains(searchTerm)
                    || d.WorkCenterCode.Contains(searchTerm)
                    || (d.WorkOrderCode != null && d.WorkOrderCode.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(typeCode)) query = query.Where(d => d.TypeCode == typeCode);
            if (dateFrom.HasValue) query = query.Where(d => d.IssueDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.IssueDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"   => query.OrderBy(d => d.ReworkMaterialDocId),
                "id_desc"  => query.OrderByDescending(d => d.ReworkMaterialDocId),
                "date_asc" => query.OrderBy(d => d.IssueDate),
                _          => query.OrderByDescending(d => d.ReworkMaterialDocId),
            };

            return View(await PaginatedList<ReworkMaterialDoc>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new ReworkMaterialDocFormVm
            {
                CreatedBy = _userContext.UserId ?? "",
                IssueDate = _dateTimeService.GetLocalDate(),
            }) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return NotFound();
            // 開單僅同步核准狀態顯示，不觸發自動過帳——過帳僅由使用者主動按「確認過帳」(Confirm) 觸發。
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            if (doc.Status == DocStatus.Posted || doc.Status == DocStatus.Void) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return NotFound();
            // 開單僅同步核准狀態顯示，不觸發自動過帳——過帳僅由使用者主動按「確認過帳」(Confirm) 觸發。
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            // 唯讀面板上的「取消確認」依此顯示（列表對非草稿只給檢視，那是唯一入口）
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(ReworkMaterialDocFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.ReworkMaterialDocId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ValidTypeCodes.Contains(vm.TypeCode))
                ModelState.AddModelError(nameof(vm.TypeCode), string.Format(_localizer["InvalidTransactionTypeRework"].Value, vm.TypeCode));
            // 分工（2026-10-16）：有開工單的重工一律走工單發退料單的重工發料／退料——它回寫工單用料的已發／已退量，
            // 料進在製品、完工時結算原值與重工損失。這張單不屬於任何工單，料發出去即認重工損失；
            // 填了既有工單號卻走這裡，工單的已發量不會動、成本也拆成兩半，所以擋下。
            var woCode = vm.WorkOrderCode?.Trim();
            if (!string.IsNullOrEmpty(woCode)
                && await _context.WorkOrders.AsNoTracking().AnyAsync(w => w.WorkOrderId == woCode))
                ModelState.AddModelError(nameof(vm.WorkOrderCode),
                    string.Format(_localizer["ReworkMaterialUseWorkOrderSlip"].Value, woCode));
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<ReworkMaterialDoc>(vm.ReworkMaterialDocId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                ReworkMaterialDoc doc;
                var isNew = string.IsNullOrEmpty(vm.ReworkMaterialDocId);
                if (isNew)
                {
                    doc = new ReworkMaterialDoc
                    {
                        ReworkMaterialDocId = await _docNumberService.NextAsync(DocType, plantId, vm.IssueDate),
                        CreatedBy = userId, PlantId = plantId,
                    };
                    _context.ReworkMaterialDocs.Add(doc);
                }
                else
                {
                    doc = await _context.ReworkMaterialDocs.Include(d => d.Lines)
                              .FirstOrDefaultAsync(d => d.ReworkMaterialDocId == vm.ReworkMaterialDocId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_ReworkMaterialNotFound"));
                    if (doc.Lines.Any(l => l.QtyPutAway > 0))
                        throw new InvalidOperationException(PzText.Get("Inv_WarehouseWorkStarted"));
                    _context.ReworkMaterialDocLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 若不 Clear，下方重新 Add 新行後過帳會把殘留的舊行一併算入，
                    // 造成沿用舊行號的行被重複過帳。
                    doc.Lines.Clear();
                }

                doc.IssueDate      = vm.IssueDate;
                doc.TypeCode       = vm.TypeCode;
                doc.WorkCenterCode = vm.WorkCenterCode;
                doc.WorkOrderCode  = vm.WorkOrderCode;
                doc.Remarks        = vm.Remarks;
                doc.UpdatedAt      = DateTime.UtcNow;
                doc.ApprovalStatus = targetStatus;

                // 來源檢核
                var typeConfig = await _context.InventoryTransactionTypes.AsNoTracking()
                    .Include(t => t.SourceType)
                    .FirstOrDefaultAsync(t => t.TypeCode == vm.TypeCode);
                if (typeConfig != null && !string.IsNullOrEmpty(typeConfig.SourceTypeCode))
                {
                    foreach (var lv in vm.Lines)
                    {
                        if (string.IsNullOrEmpty(lv.SourceDocId))
                        {
                            ModelState.AddModelError(string.Empty, string.Format(_localizer["ProductMustSelectSource"].Value, lv.ProductCode));
                            await PrepareFormDropdowns(); return PartialView("_Form", vm);
                        }
                    }
                }
                if (typeConfig?.SourceType != null)
                {
                    var sourceCategory = typeConfig.SourceType.TypeCategory;
                    foreach (var lv in vm.Lines)
                    {
                        if (string.IsNullOrEmpty(lv.SourceDocId) || lv.SourceLineNo == null) continue;
                        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, sourceCategory, lv.SourceDocId, lv.SourceLineNo.Value, vm.ReworkMaterialDocId);
                        if (remaining.HasValue && lv.Qty > remaining.Value)
                        {
                            ModelState.AddModelError(string.Empty, string.Format(_localizer["ProductReverseQtyExceeds"].Value, lv.ProductCode, lv.Qty, remaining.Value));
                            await PrepareFormDropdowns(); return PartialView("_Form", vm);
                        }
                    }
                }

                var lineNo = 1;
                foreach (var lv in vm.Lines)
                {
                    var qty = await _uomService.RoundQtyAsync(lv.UomCode, lv.Qty);
                    // 庫存單位與庫存數量一律由伺服器依品號主檔換算（2026-10-12）。原本採信畫面的 StockQty／StockUomCode，
                    // 而畫面從來不送這兩欄——選了非庫存單位（例如箱）就把「箱數」當成庫存數量、記在「箱」底下入帳。
                    var (stockUom, stockQty) = await _uomService.ToStockQtyAsync(lv.ProductCode, lv.UomCode, qty);
                    doc.Lines.Add(new ReworkMaterialDocLine
                    {
                        ReworkMaterialDocId = doc.ReworkMaterialDocId,
                        LineNo = lineNo++, ProductCode = lv.ProductCode,
                        Qty = qty, UomCode = lv.UomCode,
                        StockQty = stockQty,
                        StockUomCode = stockUom,
                        UnitPrice = lv.UnitPrice, WhCode = lv.WhCode,
                        LocationCode = lv.LocationCode, Remarks = lv.Remarks,
                        SourceDocId = lv.SourceDocId, SourceLineNo = lv.SourceLineNo,
                    });
                }

                if (targetStatus == DocumentApprovalStatus.Approved)
                    await HandleApprovedAsync(doc, plantId, userId);

                await _context.SaveChangesAsync();
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns(); return PartialView("_Form", vm);
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<ReworkMaterialDoc>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<ReworkMaterialDoc>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any())
                return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            var simplified = await IsSimplifiedAsync();
            if (simplified)
            {
                await using var tx = await _context.Database.BeginTransactionAsync();
                try
                {
                    await _postingService.PostDocSimplifiedAsync(BuildSimplifiedRequest(doc, _userContext.UserId));
                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Ok(new { success = true, message = _localizer["PostedSimple"].Value });
                }
                catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
                catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
            }

            doc.Status = DocStatus.Confirmed;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["ConfirmedAwaitingWarehouse"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Confirmed)
                return BadRequest(new { success = false, message = _localizer["ConfirmedOnlyCanWithdraw"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return BadRequest(new { success = false, message = _localizer["WarehouseStartedCannotWithdraw"].Value });
            doc.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            await using var tx1 = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = doc.PlantId ?? "", DocNo = doc.ReworkMaterialDocId,
                    Reason = reason, CreatedBy = _userContext.UserId,
                    SetDocVoid = () => doc.Status = DocStatus.Void,
                });
                await _context.SaveChangesAsync();
                await tx1.CommitAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> VoidAndReopen(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoidAndReopen"].Value });
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest { PlantId = doc.PlantId ?? "", DocNo = doc.ReworkMaterialDocId, Reason = reason ?? "作廢並重新開立", CreatedBy = _userContext.UserId, SetDocVoid = () => doc.Status = DocStatus.Void });
                var plantId = _userContext.PlantId ?? "";
                var newDoc = new ReworkMaterialDoc
                {
                    ReworkMaterialDocId = await _docNumberService.NextAsync(DocType, plantId, doc.IssueDate),
                    CreatedBy = _userContext.UserId ?? "", PlantId = plantId,
                    IssueDate = doc.IssueDate, TypeCode = doc.TypeCode,
                    WorkCenterCode = doc.WorkCenterCode, WorkOrderCode = doc.WorkOrderCode, Remarks = doc.Remarks,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
                };
                foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                    newDoc.Lines.Add(new ReworkMaterialDocLine
                    {
                        ReworkMaterialDocId = newDoc.ReworkMaterialDocId, LineNo = l.LineNo,
                        ProductCode = l.ProductCode, Qty = l.Qty, UomCode = l.UomCode,
                        StockQty = l.StockQty, StockUomCode = l.StockUomCode, UnitPrice = l.UnitPrice,
                        WhCode = l.WhCode, LocationCode = l.LocationCode, Remarks = l.Remarks,
                    });
                _context.ReworkMaterialDocs.Add(newDoc);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                await PrepareFormDropdowns();
                return PartialView("_Form", ToVm(newDoc));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasWarehouseOpsCannotDelete"].Value });
            _context.ReworkMaterialDocs.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── private helpers ─────────────────────────────────────────────────────

        private ReworkMaterialDocFormVm ToVm(ReworkMaterialDoc doc) => new()
        {
            ReworkMaterialDocId = doc.ReworkMaterialDocId,
            IssueDate = doc.IssueDate, TypeCode = doc.TypeCode,
            WorkCenterCode = doc.WorkCenterCode, WorkOrderCode = doc.WorkOrderCode,
            Remarks = doc.Remarks, CreatedBy = doc.CreatedBy,
            Status = doc.Status, ApprovalStatus = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new ReworkMaterialDocLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode,
                Qty = l.Qty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice, WhCode = l.WhCode,
                LocationCode = l.LocationCode, QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
            }).ToList(),
        };

        private async Task HandleApprovedAsync(ReworkMaterialDoc doc, string plantId, string userId)
        {
            var simplified = await IsSimplifiedAsync();
            if (simplified && doc.Status != DocStatus.Posted)
                await _postingService.PostDocSimplifiedAsync(BuildSimplifiedRequest(doc, userId));
            else if (!simplified)
                doc.Status = DocStatus.Confirmed;
        }

        private async Task<bool> IsSimplifiedAsync() => string.Equals(
            await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
            "true", StringComparison.OrdinalIgnoreCase);

        private PostDocSimplifiedRequest BuildSimplifiedRequest(ReworkMaterialDoc doc, string? userId)
        {
            var lines = doc.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var line = l;
                return new SimplifiedPostLine
                {
                    LineNo = l.LineNo, ProductCode = l.ProductCode,
                    LocationCode = l.LocationCode ?? "", WhCode = l.WhCode ?? "",
                    LotNo = "", Qty = l.StockQty,
                    UomCode = l.UomCode ?? "", StockUomCode = l.StockUomCode ?? "",
                    UnitPrice = l.UnitPrice,
                    SetQtyPutAway = qty => line.QtyPutAway = qty,
                };
            }).ToList();

            return new PostDocSimplifiedRequest
            {
                PlantId = doc.PlantId ?? "", DocNo = doc.ReworkMaterialDocId,
                MovementDate = doc.IssueDate, TypeCode = doc.TypeCode,
                TypeCategory = doc.TypeCategory, CreatedBy = userId,
                Lines = lines, SetDocPosted = () => doc.Status = DocStatus.Posted,
            };
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.ReworkMaterialDocs
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.ReworkMaterialDocId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }
    }
}
