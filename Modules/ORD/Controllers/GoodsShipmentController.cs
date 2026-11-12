using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.ORD.Controllers
{
    [Authorize]
    public class GoodsShipmentController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IInventoryPostingService _postingService;
        private readonly IInventoryService _inventoryService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IPlantParamService _plantParams;
        private readonly IARInvoiceService _arInvoiceService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.INV.Services.IInventoryReservationService? _reservations;

        private const string MenuKey = "GoodsShipment";
        private const string DocType = "GoodsShipment";

        public GoodsShipmentController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IInventoryPostingService postingService, IInventoryService inventoryService,
            IUomService uomService, ICurrencyService currencyService,
            IPlantParamService plantParams, IARInvoiceService arInvoiceService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.INV.Services.IInventoryReservationService? reservations = null)
        {
            _reservations = reservations;
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _postingService = postingService; _inventoryService = inventoryService;
            _uomService = uomService; _currencyService = currencyService;
            _plantParams = plantParams;
            _arInvoiceService = arInvoiceService;
        
            _localizer = localizer;
        }

        /// <summary>後端權威覆寫 vm.Lines 的 ExchangeRate（賣出匯率，找不到時保留原值）。</summary>
        private async Task ResolveLineRatesAsync(IEnumerable<GoodsShipmentLineVm> lines, DateOnly documentDate)
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            foreach (var lv in lines)
            {
                if (!string.IsNullOrWhiteSpace(lv.CurrencyCode))
                {
                    var rate = await _currencyService.GetRateAsync(lv.CurrencyCode, documentDate, RateType.Sell);
                    if (rate.HasValue) lv.ExchangeRate = rate.Value;
                }
                // 本幣 = 外幣 × Rate（見 currency.md §換算方向）。明細幣別可各自不同，
                // 表頭合計一律加總本欄，故此處必須以後端權威匯率重算，不採信前端傳入的 AmountLocal。
                var local = lv.TotalAmount * lv.ExchangeRate;
                lv.AmountLocal = string.IsNullOrWhiteSpace(baseCurrency)
                    ? local
                    : await _currencyService.RoundAsync(local, baseCurrency);
            }
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
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.SalesUom, p.StockUom }).ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName }).ToListAsync();

            ViewBag.CurrencyOptions = await _context.Currencies
                .AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName }).ToListAsync();

            ViewBag.TaxOptions = await _context.Taxes
                .AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.TaxCode)
                .Select(t => new { t.TaxCode, t.TaxName, t.TaxRate }).ToListAsync();

            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking().Where(w => w.IsActive && w.PlantId == _userContext.PlantId)
                .OrderBy(w => w.WhCode).Select(w => new { w.WhCode, w.WhName }).ToListAsync();


            ViewBag.UomDecimals      = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            // 明細本幣金額（AmountLocal）以本位幣代號與精度顯示、加總
            ViewBag.BaseCurrency     = await _currencyService.GetBaseCurrencyAsync() ?? "";
            ViewBag.BaseCurrencyDp   = await ResolveBaseCurrencyDpAsync();
            ViewBag.CanPrint         = await _permissionService.CanPrint(MenuKey);
            ViewBag.IsSimplified     = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, string? companyCode,
            DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;
            ViewBag.SearchTerm      = searchTerm; ViewBag.CompanyCode = companyCode;
            ViewBag.DateFrom        = dateFrom;   ViewBag.DateTo      = dateTo;
            ViewBag.Searched        = searched;   ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.IdSort          = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            await PrepareIndexPermissionsAsync(MenuKey);
            ViewBag.BaseCurrencyDp  = await ResolveBaseCurrencyDpAsync();
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();
            if (!searched) return View((PaginatedList<GoodsShipment>?)null);

            var query = _context.GoodsShipments.AsNoTracking()
                .Include(d => d.Lines).Where(d => d.PlantId == plantId);
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.GoodsShipmentId.Contains(searchTerm)
                    || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(companyCode)) query = query.Where(d => d.CompanyCode == companyCode);
            if (dateFrom.HasValue) query = query.Where(d => d.ShipDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.ShipDate <= dateTo.Value);
            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(d => d.GoodsShipmentId),
                "id_desc"   => query.OrderByDescending(d => d.GoodsShipmentId),
                "date_asc"  => query.OrderBy(d => d.ShipDate),
                "date_desc" => query.OrderByDescending(d => d.ShipDate),
                _           => query.OrderByDescending(d => d.GoodsShipmentId),
            };
            return View(await PaginatedList<GoodsShipment>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new GoodsShipmentFormVm { CreatedBy = _userContext.UserId ?? "", ShipDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns(); ViewBag.IsReadOnly = true;
            // 唯讀面板上的「取消確認」依此顯示（列表對非草稿只給檢視，那是唯一入口）
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(GoodsShipmentFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.GoodsShipmentId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId = _userContext.UserId ?? ""; var plantId = _userContext.PlantId ?? "";
                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                // 後端權威覆寫 ExchangeRate（賣出匯率）
                await ResolveLineRatesAsync(vm.Lines, vm.ShipDate);

                if (string.IsNullOrEmpty(vm.GoodsShipmentId))
                {
                    var doc = new GoodsShipment
                    {
                        GoodsShipmentId = await _docNumberService.NextAsync(DocType, plantId, vm.ShipDate),
                        ShipDate = vm.ShipDate, TypeCode = "GS",
                        CompanyCode = vm.CompanyCode, SalesOrderId = vm.SalesOrderId,
                        ShipAddress = vm.ShipAddress,
                        Remarks = vm.Remarks, CreatedBy = userId,
                        ApprovalStatus = targetStatus,
                    };
                    await ApplyLinesAsync(doc, vm); _context.GoodsShipments.Add(doc);
                    await UpdateSalesOrderShippedQtyAsync(doc);
                }
                else
                {
                    var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == vm.GoodsShipmentId)
                        ?? throw new InvalidOperationException(PzText.Get("Ord_ShipmentNotFound"));
                    // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue（見 ord-module.md §13）
                    _context.Entry(doc).Property(e => e.xmin).OriginalValue = vm.RowVersion;
                    if (doc.Status != DocStatus.Draft) throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftEditable"));
                    // 審核中不得改內容。採購單／進貨單早有這道守衛，出貨單原本漏了——
                    // 送審後照樣能改品項與數量，簽核人核准的是另一張單。
                    // 這裡刻意用 DB 讀回的 ApprovalStatus 而非 vm 帶回的值：表單的
                    // hidden 欄位是使用者可改的，信它等於這道守衛可被繞過。
                    if (GoodsShipmentStateMachine.CanEdit(doc.ApprovalStatus) != GoodsShipmentGuard.Allowed)
                        throw new InvalidOperationException(_localizer["PendingApprovalCannotEdit"].Value);
                    await ReverseShippedQtyAsync(doc);
                    doc.ShipDate = vm.ShipDate; doc.CompanyCode = vm.CompanyCode;
                    doc.SalesOrderId = vm.SalesOrderId;
                    doc.ShipAddress = vm.ShipAddress; doc.Remarks = vm.Remarks; doc.UpdatedAt = DateTime.UtcNow;
                    doc.ApprovalStatus = targetStatus;
                    _context.GoodsShipmentLines.RemoveRange(doc.Lines); doc.Lines.Clear(); await ApplyLinesAsync(doc, vm);
                    await UpdateSalesOrderShippedQtyAsync(doc);
                }
                await _context.SaveChangesAsync();
                return Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                var latest = await _context.GoodsShipments.AsNoTracking().Include(d => d.Lines)
                    .FirstOrDefaultAsync(d => d.GoodsShipmentId == vm.GoodsShipmentId);
                await PrepareFormDropdowns();
                return PartialView("_Form", latest == null ? vm : MapToVm(latest));
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            { ModelState.AddModelError(string.Empty, ex.Message); await PrepareFormDropdowns(); return PartialView("_Form", vm); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return NotFound();
            var confirmGuard = GoodsShipmentStateMachine.CanConfirm(
                doc.Status, doc.ApprovalStatus, hasLines: doc.Lines.Any());
            if (confirmGuard != GoodsShipmentGuard.Allowed)
                return BadRequest(new { success = false, message = confirmGuard switch
                {
                    GoodsShipmentGuard.AlreadyPosted => _localizer["AlreadyPostedCannotRepost"].Value,
                    GoodsShipmentGuard.AlreadyVoided => _localizer["AlreadyVoidedCannotConfirm"].Value,
                    GoodsShipmentGuard.NeedsApproval => _localizer["StagingErrNeedApproval"].Value,
                    _                                => _localizer["AddLineFirst"].Value,
                } });

            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);

            // 庫存過帳與自動產生 AR 發票須為單一原子作業：
            // 過帳已提交但發票產生失敗時整批回滾，避免「庫存已出、發票未開」的不一致。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                string message;
                if (simplified)
                {
                    // 不得拿走保留給其他需求的成品（§24.2a）：預留只在廠級，儲位庫存看不到，過帳前另外擋
                    if (_reservations != null)
                        await _reservations.EnsureShipmentWithinFreeStockAsync(doc.PlantId ?? "",
                            doc.Lines.Select(l => (l.ProductCode, l.SalesOrderId, l.SoLineNo, l.StockQty)));
                    var lines = await BuildSimplifiedLinesAsync(doc);
                    await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                    {
                        PlantId      = doc.PlantId ?? "",
                        DocNo        = doc.GoodsShipmentId,
                        MovementDate = doc.ShipDate,
                        TypeCode     = doc.TypeCode,
                        TypeCategory = "Sales",
                        CreatedBy    = _userContext.UserId,
                        Lines        = lines,
                        SetDocPosted = () => doc.Status = DocStatus.Posted,
                    });
                    await _context.SaveChangesAsync();
                    await ConsumeDemandReservationsAsync(doc.Lines.Select(l => (l, l.StockQty)));
                    message = _localizer["PostedSimple"].Value;
                }
                else
                {
                    doc.Status = DocStatus.Confirmed;
                    await _context.SaveChangesAsync();
                    message = _localizer["ConfirmedAwaitingPickup"].Value;
                }

                // 自動產生應收發票（idempotent）
                await _arInvoiceService.GenerateFromShipmentAsync(id);
                await tx.CommitAsync();
                return Ok(new { success = true, message });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (DbUpdateConcurrencyException)
            {
                return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return NotFound();
            var cancelGuard = GoodsShipmentStateMachine.CanCancelConfirm(
                doc.Status, hasPicking: doc.Lines.Any(l => l.QtyPutAway > 0));
            if (cancelGuard != GoodsShipmentGuard.Allowed)
                return BadRequest(new { success = false, message = cancelGuard switch
                {
                    GoodsShipmentGuard.NotConfirmed => _localizer["ConfirmedOnlyCanUnconfirm"].Value,
                    _                              => _localizer["HasPickingCannotUnconfirm"].Value,
                } });
            // 確認當下就自動開了應收發票（完整模式貨還沒揀）。只退回草稿的話發票留著：改數量再確認會沿用舊發票，
            // 刪掉草稿則變孤兒發票（2026-10-22）。與作廢同一原則：先由人作廢發票。
            var liveInvoice = await _context.ARInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == id && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void)
                .Select(i => i.ARInvoiceId).FirstOrDefaultAsync();
            if (liveInvoice != null)
                return BadRequest(new { success = false, message = string.Format(_localizer["ShipmentUnconfirmInvoiceFirst"].Value, liveInvoice) });
            doc.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return NotFound();
            if (GoodsShipmentStateMachine.CanVoid(doc.Status) != GoodsShipmentGuard.Allowed)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            // 作廢只沖庫存與成本，應收發票與銷退單不會跟著走：發票還活著就作廢，應收與銷貨收入留在帳上；
            // 還有有效銷退就作廢，同一批貨等於回來兩次（2026-10-22 ReceivableLedgerModelTests 發現）。先處理它們。
            var liveReturn = await _context.SalesReturnLines.AsNoTracking()
                .Where(l => l.GoodsShipmentId == id && l.SalesReturn!.Status != DocStatus.Void)
                .Select(l => l.SalesReturnId).FirstOrDefaultAsync();
            if (liveReturn != null)
                return BadRequest(new { success = false, message = string.Format(_localizer["ShipmentVoidReturnFirst"].Value, liveReturn) });
            var liveInvoice = await _context.ARInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == id && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void)
                .Select(i => i.ARInvoiceId).FirstOrDefaultAsync();
            if (liveInvoice != null)
                return BadRequest(new { success = false, message = string.Format(_localizer["ShipmentVoidInvoiceFirst"].Value, liveInvoice) });
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = doc.PlantId ?? "", DocNo = doc.GoodsShipmentId,
                    Reason = reason, CreatedBy = _userContext.UserId,
                    SetDocVoid = () => doc.Status = DocStatus.Void,
                });
                await ReverseShippedQtyAsync(doc);
                await _context.SaveChangesAsync();
                // 已出的量回到庫存：重新鎖回原訂單列（§24.2a；須在回沖已出貨量並存檔之後，上限才算得對）
                if (_reservations != null)
                    foreach (var line in doc.Lines.Where(l => !string.IsNullOrEmpty(l.SalesOrderId) && l.SoLineNo.HasValue && l.QtyPutAway > 0))
                        await _reservations.RestoreForSalesOrderLineAsync(doc.PlantId ?? "", line.SalesOrderId!,
                            line.SoLineNo!.Value, line.ProductCode, line.QtyPutAway);
                return Ok(new { success = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            { return BadRequest(new { success = false, message = ex.Message }); }
        }

        /// <summary>
        /// 出貨過帳後消耗為該訂單列保留的成品（mfg-domain.md §24.2a）。不消耗的話在手已出、預留仍掛著，
        /// 同一批貨對廠級可用量被扣兩次，直到訂單結案才釋放。
        /// </summary>
        private async Task ConsumeDemandReservationsAsync(IEnumerable<(GoodsShipmentLine Line, decimal Qty)> shipped)
        {
            if (_reservations == null) return;
            foreach (var (line, qty) in shipped)
                if (!string.IsNullOrEmpty(line.SalesOrderId) && line.SoLineNo.HasValue && qty > 0)
                    await _reservations.ConsumeForSalesOrderLineAsync(line.SalesOrderId!, line.SoLineNo.Value, line.ProductCode, qty);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<GoodsShipment>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<GoodsShipment>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.GoodsShipmentId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            var deleteGuard = GoodsShipmentStateMachine.CanDelete(doc.Status, doc.ApprovalStatus);
            if (deleteGuard != GoodsShipmentGuard.Allowed)
                return Json(new { success = false, message = deleteGuard switch
                {
                    GoodsShipmentGuard.NotDraft => _localizer["StagingErrDraftToDelete"].Value,
                    _                           => _localizer["StagingErrDeletePending"].Value,
                } });
            await ReverseShippedQtyAsync(doc);
            _context.GoodsShipments.Remove(doc); await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> AvailableLines(string? searchTerm, string? companyCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 GS 明細加總已出貨量（排除作廢）
            var shippedRaw = await _context.GoodsShipmentLines.AsNoTracking()
                .Where(gl => gl.GoodsShipment!.PlantId == plantId
                    && gl.GoodsShipment.Status != DocStatus.Void
                    && gl.SalesOrderId != null && gl.SoLineNo.HasValue)
                .GroupBy(gl => new { gl.SalesOrderId, gl.SoLineNo })
                .Select(g => new { g.Key.SalesOrderId, SoLineNo = g.Key.SoLineNo!.Value, Shipped = g.Sum(gl => gl.ShipQty) })
                .ToListAsync();
            var shippedMap = shippedRaw.ToDictionary(x => (x.SalesOrderId!, x.SoLineNo), x => x.Shipped);

            var query = _context.SalesOrderLines.AsNoTracking().Include(l => l.SalesOrder)
                .Where(l => l.SalesOrder!.PlantId == plantId
                    && l.SalesOrder.ApprovalStatus == DocumentApprovalStatus.Approved
                    && !l.IsClosed);
            if (!string.IsNullOrEmpty(companyCode)) query = query.Where(l => l.SalesOrder!.CompanyCode == companyCode);
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(l => l.SalesOrderId.ToLower().Contains(lower) || l.ProductCode.ToLower().Contains(lower));
            }
            var soLines = await query.OrderBy(l => l.SalesOrderId).ThenBy(l => l.LineNo)
                .Select(l => new
                {
                    l.SalesOrderId, l.LineNo, l.ProductCode, l.DeliveryDate,
                    l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.TaxCode, l.TaxRate,
                    CurrencyCode = l.CurrencyCode,
                    ExchangeRate  = l.ExchangeRate,
                }).Take(200).ToListAsync();

            var lines = soLines
                .Select(l =>
                {
                    var shipped   = shippedMap.TryGetValue((l.SalesOrderId, l.LineNo), out var s) ? s : 0m;
                    var remaining = l.OrderQty - shipped;
                    return new
                    {
                        l.SalesOrderId, l.LineNo, l.ProductCode, l.DeliveryDate,
                        l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                        l.UnitPrice, l.TaxCode, l.TaxRate, l.CurrencyCode, l.ExchangeRate,
                        RemainingQty = remaining,
                    };
                })
                .Where(l => l.RemainingQty > 0)
                .ToList();

            return Json(lines);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.GoodsShipments.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.GoodsShipmentId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            // 列印的合計要標幣別：明細是交易幣、表頭 TotalAmount 是本幣，
            // 兩者混在同一欄底下會讓外幣單據印出一個客戶看不懂的數字（5-9）。
            ViewBag.BaseCurrency = await _currencyService.GetBaseCurrencyAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }

        private static GoodsShipmentFormVm MapToVm(GoodsShipment doc) => new()
        {
            GoodsShipmentId = doc.GoodsShipmentId, ApprovalStatus = doc.ApprovalStatus, Status = doc.Status, RowVersion = doc.xmin,
            ShipDate = doc.ShipDate, CreatedBy = doc.CreatedBy, CompanyCode = doc.CompanyCode,
            SalesOrderId = doc.SalesOrderId,
            ShipAddress = doc.ShipAddress, Remarks = doc.Remarks,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new GoodsShipmentLineVm
            {
                LineNo = l.LineNo, SalesOrderId = l.SalesOrderId, SoLineNo = l.SoLineNo,
                ProductCode = l.ProductCode, ShipQty = l.ShipQty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice, CurrencyCode = l.CurrencyCode, ExchangeRate = l.ExchangeRate,
                TaxCode = l.TaxCode, TaxRate = l.TaxRate,
                LineAmount = l.LineAmount, TaxAmount = l.TaxAmount, TotalAmount = l.TotalAmount, AmountLocal = l.AmountLocal,
                QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
            }).ToList()
        };

        private async Task ApplyLinesAsync(GoodsShipment doc, GoodsShipmentFormVm vm)
        {
            // 庫存單位與庫存量於伺服端權威換算（不信任前端 StockQty）：
            // 單位以品號主檔 StockUom 為準，數量以 IUomService 換算後依該單位四捨五入。
            var productCodes = vm.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var stockUomMap = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);

            int seq = 1;
            foreach (var lv in vm.Lines)
            {
                var stockUom = stockUomMap.TryGetValue(lv.ProductCode, out var su) && !string.IsNullOrEmpty(su)
                    ? su!
                    : (!string.IsNullOrEmpty(lv.StockUomCode) ? lv.StockUomCode! : lv.UomCode!);
                var stockQty = string.IsNullOrEmpty(lv.UomCode) || lv.UomCode == stockUom
                    ? lv.ShipQty
                    : await _uomService.ConvertAsync(lv.ProductCode, lv.UomCode!, stockUom, lv.ShipQty);
                stockQty = await _uomService.RoundQtyAsync(stockUom, stockQty);

                doc.Lines.Add(new GoodsShipmentLine
                {
                    GoodsShipmentId = doc.GoodsShipmentId, LineNo = seq++,
                    SalesOrderId = lv.SalesOrderId, SoLineNo = lv.SoLineNo,
                    ProductCode = lv.ProductCode, ShipQty = lv.ShipQty, UomCode = lv.UomCode,
                    StockQty = stockQty,
                    StockUomCode = stockUom,
                    UnitPrice = lv.UnitPrice, CurrencyCode = lv.CurrencyCode, ExchangeRate = lv.ExchangeRate,
                    TaxCode = lv.TaxCode, TaxRate = lv.TaxRate,
                    LineAmount = lv.LineAmount, TaxAmount = lv.TaxAmount, TotalAmount = lv.TotalAmount, AmountLocal = lv.AmountLocal,
                    Remarks = lv.Remarks,
                });
            }
            doc.TotalAmount = doc.Lines.Sum(l => l.AmountLocal);
        }

        private async Task UpdateSalesOrderShippedQtyAsync(GoodsShipment doc)
        {
            foreach (var line in doc.Lines.Where(l => !string.IsNullOrEmpty(l.SalesOrderId) && l.SoLineNo.HasValue))
            {
                var soLine = await _context.SalesOrderLines.FindAsync(line.SalesOrderId, line.SoLineNo.Value);
                if (soLine == null) continue;

                var docInSoUom = await ToOrderLineUomAsync(soLine, line);

                // 上限＝訂購量，比對優先在庫存基準單位上進行。
                //
                // 出貨行的 UomCode 可以與訂單行不同（訂 PCS、出 BOX），舊寫法直接
                // 「ShippedQty + ShipQty > OrderQty」是把兩個不同單位的數字放在一起比——
                // 守門與累加一致地錯，錯得看不出來。兩張單的 StockQty 都由伺服端以
                // 品號主檔 StockUom 換算而得（見 ApplyLinesAsync／SalesOrderController
                // .ResolveLineStockQtyAsync），是唯一的共同基準。
                //
                // 訂單行的 StockQty 是 0 時退回以訂單行自己的單位比對（把出貨量換算進來），
                // 理由同 PurchaseService.UpdatePoReceivedQtyAsync：舊資料沒填基準量時，
                // 拿 0 當上限會把所有出貨都擋掉。
                //
                // 修改路徑會先 ReverseShippedQtyAsync 把舊值減掉才走到這裡，故比較的是扣除本單後的餘額。
                //
                // 基準累計本身沒被回填時（舊資料：ShippedQty 有值、ShippedStockQty 還是 0）
                // 同樣退回，否則守門會拿一個從 0 起算的累計去比上限，等於把已出的量全忘掉——
                // 那比舊的混單位錯誤更糟，是直接把守門關掉。
                var baseAccumulatorReady = soLine.ShippedStockQty > 0m || soLine.ShippedQty == 0m;
                var useStockBasis = soLine.StockQty > 0m && line.StockQty > 0m && baseAccumulatorReady;
                if (useStockBasis
                    ? soLine.ShippedStockQty + line.StockQty > soLine.StockQty
                    : soLine.ShippedQty + docInSoUom > soLine.OrderQty)
                {
                    var (acc, add, cap, uom) = useStockBasis
                        ? (soLine.ShippedStockQty, line.StockQty, soLine.StockQty, soLine.StockUomCode)
                        : (soLine.ShippedQty, docInSoUom, soLine.OrderQty, soLine.UomCode);
                    throw new InvalidOperationException(
                        PzText.Get("Ord_ShipExceedsOrderFmt", line.SalesOrderId, line.SoLineNo, acc + add, uom, cap, uom));
                }

                soLine.ShippedStockQty += line.StockQty;
                soLine.ShippedQty      += docInSoUom;
            }
        }

        /// <summary>
        /// 把出貨行的量換算成訂單行自己的單位，供 <c>ShippedQty</c>（顯示用累計）累加。
        /// 同單位時直接回傳出貨量，不繞換算——這是絕大多數情形，也避免無謂的捨入。
        /// </summary>
        private async Task<decimal> ToOrderLineUomAsync(SalesOrderLine soLine, GoodsShipmentLine line)
        {
            if (string.IsNullOrEmpty(soLine.UomCode) || soLine.UomCode == line.UomCode)
                return line.ShipQty;
            if (string.IsNullOrEmpty(line.StockUomCode) || line.StockUomCode == soLine.UomCode)
                return await _uomService.RoundQtyAsync(soLine.UomCode!, line.StockQty);

            var converted = await _uomService.ConvertAsync(
                line.ProductCode, line.StockUomCode!, soLine.UomCode!, line.StockQty);
            return await _uomService.RoundQtyAsync(soLine.UomCode!, converted);
        }

        private async Task<List<SimplifiedPostLine>> BuildSimplifiedLinesAsync(GoodsShipment doc)
        {
            var productCodes = doc.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var productToCategory = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.CategoryCode })
                .ToDictionaryAsync(p => p.ProductCode, p => p.CategoryCode);

            var categoryCodes = productToCategory.Values.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var categoryDefaults = await _context.ProductCategories.AsNoTracking()
                .Where(c => categoryCodes.Contains(c.CategoryCode))
                .Select(c => new { c.CategoryCode, c.DefaultWarehouseCode, c.DefaultLocationCode })
                .ToDictionaryAsync(c => c.CategoryCode!, c => c);

            var result = new List<SimplifiedPostLine>();
            foreach (var line in doc.Lines.OrderBy(l => l.LineNo))
            {
                var location = line.LocationCode;
                var whCode   = line.WhCode;
                if (string.IsNullOrEmpty(location))
                {
                    if (productToCategory.TryGetValue(line.ProductCode, out var catCode)
                        && !string.IsNullOrEmpty(catCode)
                        && categoryDefaults.TryGetValue(catCode, out var def))
                    {
                        location = def.DefaultLocationCode;
                        whCode ??= def.DefaultWarehouseCode;
                    }
                }
                if (string.IsNullOrEmpty(location))
                    throw new InvalidOperationException(
                        PzText.Get("Pur_LineNoLocationFmt", line.LineNo, line.ProductCode));

                var currentLine = line;
                result.Add(new SimplifiedPostLine
                {
                    LineNo       = line.LineNo,
                    ProductCode  = line.ProductCode,
                    LocationCode = location,
                    WhCode       = whCode ?? "",
                    LotNo        = line.LotNo ?? "",
                    Qty          = line.StockQty,
                    BusinessQty  = line.ShipQty,
                    UomCode      = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    UnitPrice    = line.UnitPrice,
                    SourceDocId  = line.SalesOrderId,
                    SourceLineNo = line.SoLineNo,
                    SetQtyPutAway = qty => currentLine.QtyPutAway = qty,
                });
            }
            return result;
        }

        private async Task ReverseShippedQtyAsync(GoodsShipment doc)
        {
            foreach (var line in doc.Lines.Where(l => !string.IsNullOrEmpty(l.SalesOrderId) && l.SoLineNo.HasValue))
            {
                var soLine = await _context.SalesOrderLines.FindAsync(line.SalesOrderId, line.SoLineNo.Value);
                // 下限 0：已出貨量是「累計」語意，負值沒有意義，且訂單的剩餘可出量
                // （OrderQty − ShippedQty）會因此算得比訂購量還大，超出的部分沒有任何守門會擋。
                // 目前靠刪除守門保證只回沖一次，但守門是行為、下限是不變量——與
                // PurchaseService.RestorePrOrderedQtyAsync／回沖 ReceivedQty 同一寫法。
                //
                // 兩欄必須以與累加時完全相同的換算回沖，否則「出貨→作廢」一趟下來
                // 會在訂單行留下殘值，剩餘可出量從此對不上。
                if (soLine == null) continue;
                soLine.ShippedStockQty = Math.Max(0m, soLine.ShippedStockQty - line.StockQty);
                soLine.ShippedQty      = Math.Max(0m, soLine.ShippedQty - await ToOrderLineUomAsync(soLine, line));
            }
        }
    }
}
