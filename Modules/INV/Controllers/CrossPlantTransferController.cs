using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>
    /// 跨廠調撥單。
    /// 發出（Confirm）：XP (direction=-1) 在來源廠過帳 → InTransit。
    /// 收貨（Receive）：XI (direction=+1) 在目的廠過帳 → Posted。
    /// </summary>
    [Authorize]
    public class CrossPlantTransferController : ModuleControllerBase
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

        private const string MenuKey = "CrossPlantTransfer";
        private const string DocType = "CrossPlantTransfer";

        public CrossPlantTransferController(
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

        private async Task PrepareFormDropdowns(string? toPlantId = null)
        {
            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom }).ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            // 來源廠倉庫：Warehouse 是 IPlantEntity，全域 filter 確實已限定來源廠。
            var srcWarehouses = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName }).ToListAsync();
            ViewBag.SourceWarehouseOptions = srcWarehouses;

            // 來源廠儲位：**StorageLocation 沒有 PlantId**（主鍵是 (WhCode, LocationCode)），
            // 全域廠別過濾器對它無效——原本這裡跟著倉庫寫成「全域 filter 已限定來源廠」，
            // 那句話只對 Warehouses 成立，儲位清單其實是**全公司**的。
            // 而 _Form 的 JS 新增明細列時又是從這份儲位清單反推來源倉下拉，於是
            // 撥出端的倉庫選項會出現別廠的倉（2026-09-07 實測：FG-P1／QC-P1／RM-P1／
            // FG-P2／QC-P2 都在裡面）。選下去之後伺服端拿**本廠**去查該儲位可用量，
            // 必然是 0，使用者只會看到一句莫名其妙的「可用量不足」。
            // 收斂到 StorageLocationQueryExtensions.ForCurrentPlant（同一缺口全庫共 15 處，
            // 由慣例規則 R30 守住）。
            ViewBag.SourceLocationOptions = await _context.StorageLocations
                .ForCurrentPlant(_context)
                .AsNoTracking().Where(l => l.IsActive)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();

            // 目的廠倉庫/儲位（需 IgnoreQueryFilters 跨廠存取）
            if (!string.IsNullOrEmpty(toPlantId))
            {
                ViewBag.DestWarehouseOptions = await _context.Warehouses
                    .IgnoreQueryFilters().AsNoTracking()
                    .Where(w => w.PlantId == toPlantId && w.IsActive).OrderBy(w => w.WhCode)
                    .Select(w => new { w.WhCode, w.WhName }).ToListAsync();

                ViewBag.DestLocationOptions = await _context.StorageLocations
                    .IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.IsActive)
                    .Join(_context.Warehouses.IgnoreQueryFilters().Where(w => w.PlantId == toPlantId),
                          l => l.WhCode, w => w.WhCode, (l, w) => l)
                    .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                    .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();
            }
            else
            {
                ViewBag.DestWarehouseOptions = new List<object>();
                ViewBag.DestLocationOptions  = new List<object>();
            }

            ViewBag.PlantOptions = (await _context.Plants
                .AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Name, p.NameEn }).ToListAsync())
                .Select(p => new { p.Id, Name = SmartFactory.MES.Modules._Shared.PlantDisplay.Name(p.Name, p.NameEn, p.Id) })
                .ToList();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.CurrentPlantId = _userContext.PlantId;
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        /// <summary>AJAX：依廠別載入倉庫 + 儲位選項（目的廠切換時呼叫）</summary>
        [HttpGet]
        public async Task<IActionResult> GetPlantOptions(string plantId)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            if (string.IsNullOrEmpty(plantId)) return Json(new { warehouses = Array.Empty<object>(), locations = Array.Empty<object>() });

            var warehouses = await _context.Warehouses
                .IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.PlantId == plantId && w.IsActive).OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName }).ToListAsync();

            var locations = await _context.StorageLocations
                .IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.IsActive)
                .Join(_context.Warehouses.IgnoreQueryFilters().Where(w => w.PlantId == plantId),
                      l => l.WhCode, w => w.WhCode, (l, w) => l)
                .OrderBy(l => l.WhCode).ThenBy(l => l.LocationCode)
                .Select(l => new { l.WhCode, l.LocationCode, l.LocationName }).ToListAsync();

            return Json(new { warehouses, locations });
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
            ViewBag.CurrentPlantId = _userContext.PlantId;

            if (!searched) return View((PaginatedList<CrossPlantTransfer>?)null);

            var myPlant = _userContext.PlantId ?? "";

            // IgnoreQueryFilters 以便同時看到自己作為來源廠或目的廠的單據
            var query = _context.CrossPlantTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(d => d.Lines)
                .Where(d => d.PlantId == myPlant || d.ToPlantId == myPlant)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.CrossPlantTransferId.Contains(searchTerm) || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (dateFrom.HasValue) query = query.Where(d => d.TransferDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.TransferDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"   => query.OrderBy(d => d.CrossPlantTransferId),
                "id_desc"  => query.OrderByDescending(d => d.CrossPlantTransferId),
                "date_asc" => query.OrderBy(d => d.TransferDate),
                _          => query.OrderByDescending(d => d.CrossPlantTransferId),
            };

            return View(await PaginatedList<CrossPlantTransfer>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new CrossPlantTransferFormVm
            {
                CreatedBy    = _userContext.UserId ?? "",
                TransferDate = _dateTimeService.GetLocalDate(),
                SourcePlantId = _userContext.PlantId,
            }) : RedirectToAction(nameof(Index));
        }

        /// <summary>跨廠單據僅來源廠/目的廠人員可存取——IgnoreQueryFilters 之後必須手動檢查，防跨廠 IDOR。</summary>
        private bool IsPlantParty(CrossPlantTransfer doc)
            => !string.IsNullOrEmpty(_userContext.PlantId)
               && (_userContext.PlantId == doc.PlantId || _userContext.PlantId == doc.ToPlantId);

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (!IsPlantParty(doc)) return NotFound();   // 不洩漏單據存在性
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns(doc.ToPlantId);
            if (doc.Status != DocStatus.Draft) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (!IsPlantParty(doc)) return NotFound();   // 不洩漏單據存在性
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns(doc.ToPlantId);
            ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(CrossPlantTransferFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.CrossPlantTransferId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(vm.ToPlantId); return PartialView("_Form", vm); }
            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<CrossPlantTransfer>(vm.CrossPlantTransferId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(vm.ToPlantId); return PartialView("_Form", vm);
                }

                if (string.IsNullOrEmpty(vm.ToPlantId) || vm.ToPlantId == plantId)
                {
                    ModelState.AddModelError(nameof(vm.ToPlantId), _localizer["InvalidDestinationPlant"].Value);
                    await PrepareFormDropdowns(vm.ToPlantId); return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                CrossPlantTransfer doc;
                var isNew = string.IsNullOrEmpty(vm.CrossPlantTransferId);
                if (isNew)
                {
                    doc = new CrossPlantTransfer
                    {
                        CrossPlantTransferId = await _docNumberService.NextAsync(DocType, plantId, vm.TransferDate),
                        CreatedBy = userId, PlantId = plantId,
                    };
                    _context.CrossPlantTransfers.Add(doc);
                }
                else
                {
                    doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                              .Include(d => d.Lines)
                              .FirstOrDefaultAsync(d => d.CrossPlantTransferId == vm.CrossPlantTransferId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_TransferNotFound"));
                    if (doc.PlantId != plantId)
                        throw new InvalidOperationException(PzText.Get("Inv_OnlySourcePlantCanEdit"));
                    if (doc.Status != DocStatus.Draft)
                        throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftEditable"));
                    _context.CrossPlantTransferLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 為與其餘單據一致並避免日後接上即時過帳時踩雷，明確清空。
                    doc.Lines.Clear();
                }

                doc.TransferDate   = vm.TransferDate;
                doc.FromWhCode     = vm.FromWhCode;
                doc.ToWhCode       = vm.ToWhCode;
                doc.ToPlantId      = vm.ToPlantId;
                doc.Remarks        = vm.Remarks;
                doc.UpdatedAt      = DateTime.UtcNow;
                doc.ApprovalStatus = targetStatus;

                var lineNo = 1;
                foreach (var lv in vm.Lines)
                {
                    var qty = await _uomService.RoundQtyAsync(lv.UomCode, lv.Qty);
                    // 庫存單位與庫存數量一律由伺服器依品號主檔換算（2026-10-12）。原本採信畫面的 StockQty／StockUomCode，
                    // 而畫面從來不送這兩欄——選了非庫存單位（例如箱）就把「箱數」當成庫存數量、記在「箱」底下入帳。
                    var (stockUom, stockQty) = await _uomService.ToStockQtyAsync(lv.ProductCode, lv.UomCode, qty);
                    doc.Lines.Add(new CrossPlantTransferLine
                    {
                        CrossPlantTransferId = doc.CrossPlantTransferId,
                        LineNo           = lineNo++,
                        ProductCode      = lv.ProductCode,
                        Qty              = qty,
                        UomCode          = lv.UomCode,
                        StockQty         = stockQty,
                        StockUomCode     = stockUom,
                        FromWhCode       = lv.FromWhCode ?? vm.FromWhCode,
                        FromLocationCode = lv.FromLocationCode,
                        ToWhCode         = lv.ToWhCode ?? vm.ToWhCode,
                        ToLocationCode   = lv.ToLocationCode,
                        LotNo            = string.IsNullOrWhiteSpace(lv.LotNo) ? null : lv.LotNo.Trim(),
                        Remarks          = lv.Remarks,
                    });
                }

                await _context.SaveChangesAsync();
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns(vm.ToPlantId); return PartialView("_Form", vm);
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (doc.PlantId != _userContext.PlantId)
                return BadRequest(new { success = false, message = _localizer["SourcePlantOnlyCanSubmit"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Draft && doc.ApprovalStatus != DocumentApprovalStatus.Rejected)
                return BadRequest(new { success = false, message = _localizer["StagingErrDraftOrReject"].Value });
            var plantId = _userContext.PlantId ?? "";
            var config  = await _approvalService.GetApprovalConfig(DocType, plantId);
            if (config?.ApprovalTemplateId == null)
            {
                doc.ApprovalStatus = DocumentApprovalStatus.Approved;
                await _context.SaveChangesAsync();
                return Ok(new { success = true, message = _localizer["StagingApprovedNoWorkflow"].Value });
            }
            var request = await _approvalService.SubmitForApproval(id, DocType, config.ApprovalTemplateId, _userContext.UserId ?? "");
            doc.ApprovalRequestId = request.Id;
            doc.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingSubmitted"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (doc.PlantId != _userContext.PlantId)
                return BadRequest(new { success = false, message = _localizer["SourcePlantOnlyCanWithdraw"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.PendingApproval)
                return BadRequest(new { success = false, message = _localizer["StagingErrMustBePending"].Value });
            var ok = await _approvalService.WithdrawRequest(doc.ApprovalRequestId!, _userContext.UserId ?? "");
            if (!ok) return BadRequest(new { success = false, message = _localizer["StagingWithdrawFailed"].Value });
            doc.ApprovalStatus = DocumentApprovalStatus.Draft; doc.ApprovalRequestId = null;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        /// <summary>來源廠確認發出：XP 過帳 → InTransit</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (doc.PlantId != _userContext.PlantId)
                return BadRequest(new { success = false, message = _localizer["SourcePlantOnlyCanConfirmOut"].Value });
            if (doc.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["DraftOnlyCanConfirmOut"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any())
                return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await PostXpAsync(doc, _userContext.UserId);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["CrossPlantDispatched"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        /// <summary>目的廠確認收貨：XI 過帳 → Posted</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Receive(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (doc.ToPlantId != _userContext.PlantId)
                return BadRequest(new { success = false, message = _localizer["DestPlantOnlyCanReceive"].Value });
            if (doc.Status != DocStatus.InTransit)
                return BadRequest(new { success = false, message = _localizer["InTransitOnlyCanReceive"].Value });

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await PostXiAsync(doc, _userContext.UserId);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["CrossPlantReceived"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (doc.PlantId != _userContext.PlantId)
                return BadRequest(new { success = false, message = _localizer["SourcePlantOnlyCanWithdrawOut"].Value });
            if (doc.Status != DocStatus.InTransit)
                return BadRequest(new { success = false, message = _localizer["InTransitOnlyCanWithdraw"].Value });
            if (doc.Lines.Any(l => l.QtyReceived > 0))
                return BadRequest(new { success = false, message = _localizer["DestPlantReceivedCannotWithdraw"].Value });

            var movements = await _context.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == id && m.TypeCode == "XP").ToListAsync();

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidByMovementsAsync(movements, new VoidByMovementsRequest
                {
                    VoidDate = _dateTimeService.GetLocalDate(),
                    CreatedBy = _userContext.UserId,
                });
                foreach (var l in doc.Lines) l.QtyShipped = 0;
                doc.Status = DocStatus.Draft;
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (!IsPlantParty(doc)) return NotFound();   // 不洩漏單據存在性
            if (doc.Status != DocStatus.Posted)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });

            var movements = await _context.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == id && (m.TypeCode == "XP" || m.TypeCode == "XI"))
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
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers.IgnoreQueryFilters()
                .Include(d => d.Lines).FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.PlantId != _userContext.PlantId)
                return Json(new { success = false, message = _localizer["SourcePlantOnlyCanDelete"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Status != DocStatus.Draft)
                return Json(new { success = false, message = _localizer["StagingErrDraftToDelete"].Value });
            _context.CrossPlantTransfers.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── private helpers ─────────────────────────────────────────────────────

        private CrossPlantTransferFormVm ToVm(CrossPlantTransfer doc) => new()
        {
            CrossPlantTransferId = doc.CrossPlantTransferId,
            TransferDate      = doc.TransferDate,
            FromWhCode        = doc.FromWhCode, ToWhCode = doc.ToWhCode,
            ToPlantId         = doc.ToPlantId,
            SourcePlantId     = doc.PlantId,
            Remarks           = doc.Remarks, CreatedBy = doc.CreatedBy,
            Status            = doc.Status,  ApprovalStatus = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new CrossPlantTransferLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode,
                Qty = l.Qty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                FromWhCode       = l.FromWhCode,
                FromLocationCode = l.FromLocationCode,
                ToWhCode         = l.ToWhCode,
                ToLocationCode   = l.ToLocationCode,
                LotNo            = l.LotNo,
                QtyShipped   = l.QtyShipped,
                QtyReceived  = l.QtyReceived,
                Remarks = l.Remarks,
            }).ToList(),
        };

        private Task PostXpAsync(CrossPlantTransfer doc, string? userId) =>
            _postingService.PostCrossPlantShipAsync(new PostCrossPlantShipRequest
            {
                PlantId = doc.PlantId ?? "",
                DocNo = doc.CrossPlantTransferId,
                MovementDate = doc.TransferDate,
                CreatedBy = userId,
                Lines = doc.Lines.OrderBy(l => l.LineNo).Select(line => new CrossPlantShipLine
                {
                    LineNo = line.LineNo,
                    ProductCode = line.ProductCode,
                    FromLocationCode = line.FromLocationCode ?? "",
                    LotNo = line.LotNo ?? "",
                    StockQty = line.StockQty,
                    UomCode = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    SetQtyShipped = qty => { line.QtyShipped = qty; },
                }).ToList(),
                SetDocInTransit = () => { doc.Status = DocStatus.InTransit; },
            });

        private Task PostXiAsync(CrossPlantTransfer doc, string? userId) =>
            _postingService.PostCrossPlantReceiveAsync(new PostCrossPlantReceiveRequest
            {
                ToPlantId = doc.ToPlantId ?? "",
                DocNo = doc.CrossPlantTransferId,
                MovementDate = doc.TransferDate,
                CreatedBy = userId,
                Lines = doc.Lines.OrderBy(l => l.LineNo).Select(line => new CrossPlantReceiveLine
                {
                    LineNo = line.LineNo,
                    ProductCode = line.ProductCode,
                    ToLocationCode = line.ToLocationCode ?? "",
                    LotNo = line.LotNo ?? "",
                    StockQty = line.StockQty,
                    UomCode = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    SetQtyReceived = qty => { line.QtyReceived = qty; },
                }).ToList(),
                SetDocPosted = () => { doc.Status = DocStatus.Posted; },
            });

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.CrossPlantTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.CrossPlantTransferId == id);
            if (doc == null) return NotFound();
            if (!IsPlantParty(doc)) return NotFound();   // 不洩漏單據存在性
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            // 品號／單位走共用查表；倉庫跨兩廠，依廠別各自查名（同代碼在兩廠可能是不同倉）
            var n = await PrintNames.ForAsync(doc.Lines.Select(l => new { l.ProductCode, l.UomCode }).ToList());
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var plants = new[] { doc.PlantId, doc.ToPlantId };
            var whs = await _context.Warehouses.IgnoreQueryFilters().AsNoTracking()
                .Where(w => plants.Contains(w.PlantId))
                .Select(w => new { w.PlantId, w.WhCode, w.WhName }).ToListAsync();
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var plantNames = await _context.Plants.AsNoTracking()
                .Where(x => plants.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => !isZh && !string.IsNullOrWhiteSpace(x.NameEn) ? x.NameEn! : x.Name);
            string? Wh(string? plant, string? code)
            {
                if (string.IsNullOrEmpty(code)) return null;
                var name = whs.FirstOrDefault(w => w.PlantId == plant && w.WhCode == code)?.WhName;
                return string.IsNullOrEmpty(name) || name == code ? code : $"{code} {name}";
            }
            string? Place(string? plant, string? wh, string? loc) =>
                string.Join(" / ", new[] { Wh(plant, wh), loc }.Where(x => !string.IsNullOrEmpty(x)));
            string? Plant(string? pid) => pid == null ? null : plantNames.TryGetValue(pid, out var pn) ? $"{pid} {pn}" : pid;
            var p = new PrintDocument
            {
                Title = L["Print_Title_CrossPlantTransfer"], DocNo = doc.CrossPlantTransferId,
                DateLabel = L["Print_TransferDate"], DateValue = doc.TransferDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_FromPlant"], Plant(doc.PlantId))
                .Field(L["Print_FromWarehouse"], Wh(doc.PlantId, doc.FromWhCode))
                .Field(L["Print_ToPlant"], Plant(doc.ToPlantId))
                .Field(L["Print_ToWarehouse"], Wh(doc.ToPlantId, doc.ToWhCode))
                .Field(L["Print_Status"], DocEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_Qty"], true).Col(L["Print_Uom"]).Col(L["Print_From"]).Col(L["Print_To"]).Col(L["Print_LotNo"])
                .Col(L["Print_QtyShipped"], true).Col(L["Print_QtyReceived"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), l.ProductCode, n.Product(l.ProductCode),
                    f.Qty(l.Qty, l.UomCode), n.UomWith(l.UomCode),
                    Place(doc.PlantId, l.FromWhCode ?? doc.FromWhCode, l.FromLocationCode),
                    Place(doc.ToPlantId, l.ToWhCode ?? doc.ToWhCode, l.ToLocationCode), l.LotNo ?? "",
                    f.Qty(l.QtyShipped, l.StockUomCode), f.Qty(l.QtyReceived, l.StockUomCode));
            p.Tables.Add(tbl);
            return PrintDocumentView(p);
        }
    }
}
