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
    /// <summary>委外發退料單 (OO/OR)。</summary>
    [Authorize]
    public class OutsourceMaterialController : ModuleControllerBase
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

        private const string MenuKey = "OutsourceMaterial";
        private const string DocType = "OutsourceMaterial";

        private static readonly HashSet<string> ValidTypeCodes = new() { "OO", "OR" };

        public OutsourceMaterialController(
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

            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsSupplier && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            ViewBag.OpoOptions = await _context.OutsourcePurchaseOrders
                .AsNoTracking().OrderByDescending(o => o.OutsourcePurchaseOrderId)
                .Select(o => new { o.OutsourcePurchaseOrderId, o.SupplierCode }).ToListAsync();

            ViewBag.DispatchOptions = await _context.Dispatches
                .AsNoTracking().Where(d => d.IsOutsource).OrderByDescending(d => d.DispatchId)
                .Select(d => d.DispatchId).ToListAsync();

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

            if (!searched) return View((PaginatedList<OutsourceMaterialDoc>?)null);

            var query = _context.OutsourceMaterialDocs.AsNoTracking().Include(d => d.Lines).AsQueryable();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.OutsourceMaterialDocId.Contains(searchTerm)
                    || d.SupplierCode.Contains(searchTerm)
                    || (d.WorkOrderCode != null && d.WorkOrderCode.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(typeCode)) query = query.Where(d => d.TypeCode == typeCode);
            if (dateFrom.HasValue) query = query.Where(d => d.IssueDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.IssueDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"   => query.OrderBy(d => d.OutsourceMaterialDocId),
                "id_desc"  => query.OrderByDescending(d => d.OutsourceMaterialDocId),
                "date_asc" => query.OrderBy(d => d.IssueDate),
                _          => query.OrderByDescending(d => d.OutsourceMaterialDocId),
            };

            return View(await PaginatedList<OutsourceMaterialDoc>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new OutsourceMaterialDocFormVm
            {
                CreatedBy = _userContext.UserId ?? "",
                IssueDate = _dateTimeService.GetLocalDate(),
            }) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
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
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
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
        public async Task<IActionResult> Save(OutsourceMaterialDocFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.OutsourceMaterialDocId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ValidTypeCodes.Contains(vm.TypeCode))
                ModelState.AddModelError(nameof(vm.TypeCode), string.Format(_localizer["InvalidTransactionTypeOutsource"].Value, vm.TypeCode));
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<OutsourceMaterialDoc>(vm.OutsourceMaterialDocId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                // MFG 連結檢核（pur-domain.md §4.3：OPO/派工存在性、廠商一致、OO 發料量守門）
                // 放在建立實體之前：失敗不耗用單號、不在追蹤器殘留半套實體。
                var mfgLinkError = await ValidateMfgLinksAsync(vm);
                if (mfgLinkError != null)
                {
                    ModelState.AddModelError(string.Empty, mfgLinkError);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                OutsourceMaterialDoc doc;
                var isNew = string.IsNullOrEmpty(vm.OutsourceMaterialDocId);
                if (isNew)
                {
                    doc = new OutsourceMaterialDoc
                    {
                        OutsourceMaterialDocId = await _docNumberService.NextAsync(DocType, plantId, vm.IssueDate),
                        CreatedBy = userId, PlantId = plantId,
                    };
                    _context.OutsourceMaterialDocs.Add(doc);
                }
                else
                {
                    doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines)
                              .FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == vm.OutsourceMaterialDocId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_OutsourceMaterialNotFound"));
                    if (doc.Lines.Any(l => l.QtyPutAway > 0))
                        throw new InvalidOperationException(PzText.Get("Inv_WarehouseWorkStarted"));
                    _context.OutsourceMaterialDocLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 若不 Clear，下方重新 Add 新行後過帳會把殘留的舊行一併算入，
                    // 造成沿用舊行號的行被重複過帳。
                    doc.Lines.Clear();
                }

                doc.IssueDate     = vm.IssueDate;
                doc.TypeCode      = vm.TypeCode;
                doc.SupplierCode  = vm.SupplierCode;
                doc.WorkOrderCode = vm.WorkOrderCode;
                doc.OutsourcePurchaseOrderId = string.IsNullOrWhiteSpace(vm.OutsourcePurchaseOrderId) ? null : vm.OutsourcePurchaseOrderId;
                doc.DispatchId    = string.IsNullOrWhiteSpace(vm.DispatchId) ? null : vm.DispatchId;
                doc.Remarks       = vm.Remarks;
                doc.UpdatedAt     = DateTime.UtcNow;
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
                        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, sourceCategory, lv.SourceDocId, lv.SourceLineNo.Value, vm.OutsourceMaterialDocId);
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
                    doc.Lines.Add(new OutsourceMaterialDocLine
                    {
                        OutsourceMaterialDocId = doc.OutsourceMaterialDocId,
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
            => await SubmitForApprovalAsync<OutsourceMaterialDoc>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<OutsourceMaterialDoc>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
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
                    // 委外退料以在外原料的淨加權成本收回（單價欄位隱藏、新增時為空）
                    await new Modules.PUR.Services.OutsourceMaterialFlowReader(_context).FillReturnCostsAsync(doc);
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
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
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
            var doc = await _context.OutsourceMaterialDocs.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            await using var tx1 = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = doc.PlantId ?? "", DocNo = doc.OutsourceMaterialDocId,
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
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoidAndReopen"].Value });
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest { PlantId = doc.PlantId ?? "", DocNo = doc.OutsourceMaterialDocId, Reason = reason ?? "作廢並重新開立", CreatedBy = _userContext.UserId, SetDocVoid = () => doc.Status = DocStatus.Void });
                var plantId = _userContext.PlantId ?? "";
                var newDoc = new OutsourceMaterialDoc
                {
                    OutsourceMaterialDocId = await _docNumberService.NextAsync(DocType, plantId, doc.IssueDate),
                    CreatedBy = _userContext.UserId ?? "", PlantId = plantId,
                    IssueDate = doc.IssueDate, TypeCode = doc.TypeCode,
                    SupplierCode = doc.SupplierCode, WorkOrderCode = doc.WorkOrderCode, Remarks = doc.Remarks,
                    OutsourcePurchaseOrderId = doc.OutsourcePurchaseOrderId, DispatchId = doc.DispatchId,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
                };
                foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                    newDoc.Lines.Add(new OutsourceMaterialDocLine
                    {
                        OutsourceMaterialDocId = newDoc.OutsourceMaterialDocId, LineNo = l.LineNo,
                        ProductCode = l.ProductCode, Qty = l.Qty, UomCode = l.UomCode,
                        StockQty = l.StockQty, StockUomCode = l.StockUomCode, UnitPrice = l.UnitPrice,
                        WhCode = l.WhCode, LocationCode = l.LocationCode, Remarks = l.Remarks,
                    });
                _context.OutsourceMaterialDocs.Add(newDoc);
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
            var doc = await _context.OutsourceMaterialDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasWarehouseOpsCannotDelete"].Value });
            _context.OutsourceMaterialDocs.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── private helpers ─────────────────────────────────────────────────────

        /// <summary>pur-domain.md §4.3 MFG 連結檢核；通過回傳 null，否則回傳錯誤訊息。</summary>
        private async Task<string?> ValidateMfgLinksAsync(OutsourceMaterialDocFormVm vm)
        {
            if (!string.IsNullOrWhiteSpace(vm.OutsourcePurchaseOrderId))
            {
                var opo = await _context.OutsourcePurchaseOrders.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.OutsourcePurchaseOrderId == vm.OutsourcePurchaseOrderId);
                if (opo == null)
                    return string.Format(_localizer["OpoNotFound"].Value, vm.OutsourcePurchaseOrderId);
                if (!string.Equals(opo.SupplierCode, vm.SupplierCode, StringComparison.Ordinal))
                    return string.Format(_localizer["OpoSupplierMismatch"].Value, vm.OutsourcePurchaseOrderId, opo.SupplierCode);
            }

            if (string.IsNullOrWhiteSpace(vm.DispatchId)) return null;

            var dispatchExists = await _context.Dispatches.AsNoTracking()
                .AnyAsync(d => d.DispatchId == vm.DispatchId && d.IsOutsource);
            if (!dispatchExists)
                return string.Format(_localizer["DispatchNotFound"].Value, vm.DispatchId);

            // OO 發料量守門：各品號本單量 ≤ RequiredQty − 其他未作廢 OO 單已發量
            if (vm.TypeCode != "OO") return null;

            var materials = await _context.DispatchMaterials.AsNoTracking()
                .Where(m => m.DispatchId == vm.DispatchId).ToListAsync();

            var issuedByOthers = await _context.OutsourceMaterialDocLines.AsNoTracking()
                .Where(l => l.OutsourceMaterialDoc!.DispatchId == vm.DispatchId
                    && l.OutsourceMaterialDoc.TypeCode == "OO"
                    && l.OutsourceMaterialDoc.Status != DocStatus.Void
                    && l.OutsourceMaterialDocId != vm.OutsourceMaterialDocId)
                .GroupBy(l => l.ProductCode)
                .Select(g => new { ProductCode = g.Key, Qty = g.Sum(l => l.Qty) })
                .ToDictionaryAsync(g => g.ProductCode, g => g.Qty);

            foreach (var grp in vm.Lines.GroupBy(l => l.ProductCode))
            {
                var dm = materials.FirstOrDefault(m => m.ProductCode == grp.Key);
                if (dm == null)
                    return string.Format(_localizer["ProductNotInDispatch"].Value, grp.Key, vm.DispatchId);
                var remaining = dm.RequiredQty - issuedByOthers.GetValueOrDefault(grp.Key);
                var thisDocQty = grp.Sum(l => l.Qty);
                if (thisDocQty > remaining)
                    return string.Format(_localizer["OutsourceIssueExceedsRequired"].Value, grp.Key, thisDocQty, remaining);
            }
            return null;
        }

        private OutsourceMaterialDocFormVm ToVm(OutsourceMaterialDoc doc) => new()
        {
            OutsourceMaterialDocId = doc.OutsourceMaterialDocId,
            IssueDate      = doc.IssueDate, TypeCode = doc.TypeCode,
            SupplierCode   = doc.SupplierCode, WorkOrderCode = doc.WorkOrderCode,
            OutsourcePurchaseOrderId = doc.OutsourcePurchaseOrderId, DispatchId = doc.DispatchId,
            Remarks        = doc.Remarks, CreatedBy = doc.CreatedBy,
            Status         = doc.Status, ApprovalStatus = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new OutsourceMaterialDocLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode,
                Qty = l.Qty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice, WhCode = l.WhCode,
                LocationCode = l.LocationCode, QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
            }).ToList(),
        };

        private async Task HandleApprovedAsync(OutsourceMaterialDoc doc, string plantId, string userId)
        {
            var simplified = await IsSimplifiedAsync();
            if (simplified && doc.Status != DocStatus.Posted)
            {
                await new Modules.PUR.Services.OutsourceMaterialFlowReader(_context).FillReturnCostsAsync(doc);
                await _postingService.PostDocSimplifiedAsync(BuildSimplifiedRequest(doc, userId));
            }
            else if (!simplified)
                doc.Status = DocStatus.Confirmed;
        }

        private async Task<bool> IsSimplifiedAsync() => string.Equals(
            await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
            "true", StringComparison.OrdinalIgnoreCase);

        private PostDocSimplifiedRequest BuildSimplifiedRequest(OutsourceMaterialDoc doc, string? userId)
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
                PlantId = doc.PlantId ?? "", DocNo = doc.OutsourceMaterialDocId,
                MovementDate = doc.IssueDate, TypeCode = doc.TypeCode,
                TypeCategory = doc.TypeCategory, CreatedBy = userId,
                Lines = lines, SetDocPosted = () => doc.Status = DocStatus.Posted,
            };
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.OutsourceMaterialDocs
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }
    }
}
