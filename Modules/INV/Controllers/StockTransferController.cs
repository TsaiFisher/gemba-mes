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
    /// <summary>同廠調撥單 (WP+WI 成對異動)。</summary>
    [Authorize]
    public class StockTransferController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "StockTransfer";
        private const string DocType = "StockTransfer";

        public StockTransferController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IDocNumberService docNumberService, IInventoryPostingService postingService,
            IUomService uomService, ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService;
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

            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName }).ToListAsync();

            ViewBag.LocationOptions = await _context.StorageLocations
                .ForCurrentPlant(_context)
                .AsNoTracking().Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.SourceTypeCode = await _context.InventoryTransactionTypes.AsNoTracking()
                .Where(t => t.TypeCode == "WT").Select(t => t.SourceTypeCode).FirstOrDefaultAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm; ViewBag.DateFrom = dateFrom;
            ViewBag.DateTo = dateTo; ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20; ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<StockTransfer>?)null);

            var query = _context.StockTransfers.AsNoTracking().Include(d => d.Lines).AsQueryable();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.StockTransferId.Contains(searchTerm) || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (dateFrom.HasValue) query = query.Where(d => d.TransferDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.TransferDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"   => query.OrderBy(d => d.StockTransferId),
                "id_desc"  => query.OrderByDescending(d => d.StockTransferId),
                "date_asc" => query.OrderBy(d => d.TransferDate),
                _          => query.OrderByDescending(d => d.StockTransferId),
            };

            return View(await PaginatedList<StockTransfer>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new StockTransferFormVm
            {
                CreatedBy    = _userContext.UserId ?? "",
                TransferDate = _dateTimeService.GetLocalDate(),
            }) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc, () => OnStockTransferApprovedAsync(doc));
            await PrepareFormDropdowns();
            if (doc.Status == DocStatus.Posted || doc.Status == DocStatus.Void) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc, () => OnStockTransferApprovedAsync(doc));
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(StockTransferFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.StockTransferId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<StockTransfer>(vm.StockTransferId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                StockTransfer doc;
                var isNew = string.IsNullOrEmpty(vm.StockTransferId);
                if (isNew)
                {
                    doc = new StockTransfer
                    {
                        StockTransferId = await _docNumberService.NextAsync(DocType, plantId, vm.TransferDate),
                        CreatedBy = userId, PlantId = plantId,
                    };
                    _context.StockTransfers.Add(doc);
                }
                else
                {
                    doc = await _context.StockTransfers.Include(d => d.Lines)
                              .FirstOrDefaultAsync(d => d.StockTransferId == vm.StockTransferId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_TransferNotFound"));
                    if (doc.Lines.Any(l => l.QtyPutAway > 0))
                        throw new InvalidOperationException(PzText.Get("Inv_PartiallyPosted"));
                    _context.StockTransferLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 若不 Clear，下方重新 Add 新行後過帳會把殘留的舊行一併算入，
                    // 造成沿用舊行號的行被重複過帳。
                    doc.Lines.Clear();
                }

                doc.TransferDate   = vm.TransferDate;
                doc.FromWhCode     = vm.FromWhCode;
                doc.ToWhCode       = vm.ToWhCode;
                doc.Remarks        = vm.Remarks;
                doc.UpdatedAt      = DateTime.UtcNow;
                doc.ApprovalStatus = targetStatus;

                // 來源檢核
                var typeConfig = await _context.InventoryTransactionTypes.AsNoTracking()
                    .Include(t => t.SourceType)
                    .FirstOrDefaultAsync(t => t.TypeCode == "WT");
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
                        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, sourceCategory, lv.SourceDocId, lv.SourceLineNo.Value, vm.StockTransferId);
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
                    doc.Lines.Add(new StockTransferLine
                    {
                        StockTransferId  = doc.StockTransferId,
                        LineNo           = lineNo++,
                        ProductCode      = lv.ProductCode,
                        Qty              = qty,
                        UomCode          = lv.UomCode,
                        StockQty         = stockQty,
                        StockUomCode     = stockUom,
                        UnitPrice        = lv.UnitPrice,
                        FromWhCode       = lv.FromWhCode ?? vm.FromWhCode,
                        FromLocationCode = lv.FromLocationCode,
                        ToWhCode         = lv.ToWhCode ?? vm.ToWhCode,
                        ToLocationCode   = lv.ToLocationCode,
                        LotNo            = string.IsNullOrWhiteSpace(lv.LotNo) ? null : lv.LotNo.Trim(),
                        ToLotNo          = string.IsNullOrWhiteSpace(lv.ToLotNo) ? null : lv.ToLotNo.Trim(),
                        Remarks          = lv.Remarks,
                        SourceDocId      = lv.SourceDocId, SourceLineNo = lv.SourceLineNo,
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
            => await SubmitForApprovalAsync<StockTransfer>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<StockTransfer>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any())
                return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await PostTransferCoreAsync(doc, _userContext.UserId);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["Posted"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
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
            var doc = await _context.StockTransfers.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });

            var movements = await _context.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == id && (m.TypeCode == "WP" || m.TypeCode == "WI"))
                .ToListAsync();

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidByMovementsAsync(movements, new VoidByMovementsRequest
                {
                    VoidDate = _dateTimeService.GetLocalDate(),
                    Reason = reason,
                    CreatedBy = _userContext.UserId,
                    SetDocVoid = () => { doc.Status = DocStatus.Void; },
                });
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> VoidAndReopen(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoidAndReopen"].Value });
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var movements = await _context.StockMovements.IgnoreQueryFilters()
                    .Where(m => m.DocNo == id && (m.TypeCode == "WP" || m.TypeCode == "WI")).ToListAsync();
                await _postingService.VoidByMovementsAsync(movements, new VoidByMovementsRequest
                {
                    VoidDate = _dateTimeService.GetLocalDate(),
                    Reason = reason,
                    CreatedBy = _userContext.UserId,
                    SetDocVoid = () => { doc.Status = DocStatus.Void; },
                });
                var plantId = _userContext.PlantId ?? "";
                var newDoc = new StockTransfer
                {
                    StockTransferId = await _docNumberService.NextAsync(DocType, plantId, doc.TransferDate),
                    CreatedBy = _userContext.UserId ?? "", PlantId = plantId,
                    TransferDate = doc.TransferDate, FromWhCode = doc.FromWhCode, ToWhCode = doc.ToWhCode, Remarks = doc.Remarks,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
                };
                foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                    newDoc.Lines.Add(new StockTransferLine
                    {
                        StockTransferId = newDoc.StockTransferId, LineNo = l.LineNo,
                        ProductCode = l.ProductCode, Qty = l.Qty, UomCode = l.UomCode,
                        StockQty = l.StockQty, StockUomCode = l.StockUomCode, UnitPrice = l.UnitPrice,
                        FromWhCode = l.FromWhCode, FromLocationCode = l.FromLocationCode,
                        ToWhCode = l.ToWhCode, ToLocationCode = l.ToLocationCode, LotNo = l.LotNo, ToLotNo = l.ToLotNo, Remarks = l.Remarks,
                    });
                _context.StockTransfers.Add(newDoc);
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
            var doc = await _context.StockTransfers.Include(d => d.Lines).FirstOrDefaultAsync(d => d.StockTransferId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasPostedLinesCannotDelete"].Value });
            _context.StockTransfers.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── private helpers ─────────────────────────────────────────────────────

        private StockTransferFormVm ToVm(StockTransfer doc) => new()
        {
            StockTransferId   = doc.StockTransferId,
            TransferDate      = doc.TransferDate,
            FromWhCode        = doc.FromWhCode, ToWhCode = doc.ToWhCode,
            Remarks           = doc.Remarks,    CreatedBy = doc.CreatedBy,
            Status            = doc.Status,     ApprovalStatus = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new StockTransferLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode,
                Qty = l.Qty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice,
                FromWhCode = l.FromWhCode, FromLocationCode = l.FromLocationCode,
                ToWhCode   = l.ToWhCode,   ToLocationCode   = l.ToLocationCode,
                LotNo      = l.LotNo,
                ToLotNo    = l.ToLotNo,
                QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
            }).ToList(),
        };

        private async Task OnStockTransferApprovedAsync(StockTransfer doc)
        {
            if (doc.Status != DocStatus.Draft) return;
            // 自動過帳例外不在此吞掉：交由 SyncApprovalStatusAsync 統一以交易回滾並提示。
            await PostTransferCoreAsync(doc, _userContext.UserId);
        }

        private async Task HandleApprovedAsync(StockTransfer doc, string plantId, string userId)
        {
            if (doc.Status != DocStatus.Posted)
                await PostTransferCoreAsync(doc, userId);
        }

        private Task PostTransferCoreAsync(StockTransfer doc, string? userId) =>
            _postingService.PostSamePlantTransferAsync(new PostSamePlantTransferRequest
            {
                PlantId = doc.PlantId ?? "",
                DocNo = doc.StockTransferId,
                MovementDate = doc.TransferDate,
                CreatedBy = userId,
                Lines = doc.Lines.OrderBy(l => l.LineNo).Select(line => new TransferLine
                {
                    LineNo = line.LineNo,
                    ProductCode = line.ProductCode,
                    FromLocationCode = line.FromLocationCode ?? "",
                    ToLocationCode = line.ToLocationCode ?? "",
                    LotNo = line.LotNo ?? "",
                    ToLotNo = line.ToLotNo,
                    StockQty = line.StockQty,
                    UomCode = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    SetQtyPutAway = qty => { line.QtyPutAway = qty; },
                }).ToList(),
                SetDocPosted = () => { doc.Status = DocStatus.Posted; },
            });

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.StockTransfers
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.StockTransferId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }
    }
}
