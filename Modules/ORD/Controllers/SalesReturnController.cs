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
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.ORD.Controllers
{
    [Authorize]
    public class SalesReturnController : ModuleControllerBase
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
        private readonly IPlantParamService _plantParams;
        private readonly ISamplingPlanService _sampling;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.FIN.Services.IARInvoiceService? _arInvoiceService;   // optional：FIN 未啟用時為 null
        private readonly Modules.FIN.Services.IARCreditNoteService? _creditNoteService;  // 同上

        private const string MenuKey = "SalesReturn";
        private const string DocType = "SalesReturn";

        public SalesReturnController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IInventoryPostingService postingService,
            IUomService uomService, ICurrencyService currencyService,
            IPlantParamService plantParams,
            ISamplingPlanService sampling,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.FIN.Services.IARInvoiceService? arInvoiceService = null,
            Modules.FIN.Services.IARCreditNoteService? creditNoteService = null)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _postingService = postingService; _uomService = uomService; _currencyService = currencyService;
            _plantParams = plantParams;
            _sampling = sampling;

            _localizer = localizer;
            _arInvoiceService = arInvoiceService;
            _creditNoteService = creditNoteService;
        }

        /// <summary>
        /// 銷貨退回「完成（過帳）」後置：以退貨量負向回沖來源出貨單的應收發票（降低應收）。
        /// 只處理有連結來源出貨明細（ShipmentLineNo）的退回行；ReturnQty 與 AR 開立同為銷售單位。
        /// 手工加入、未連結來源出貨的行不回沖——與 GoodsReturn 的 AP 側同一守則。
        ///
        /// 回傳**沒有回沖掉**的金額合計（本位幣含稅）。來源發票已收款時服務層不動它
        /// （改已結清發票的金額會讓憑證與稅務申報前後不一致），但那筆錢公司實際上欠客戶——
        /// 原本這裡把回傳值整個丟掉，於是缺口在系統裡完全不留痕跡。
        /// </summary>
        private async Task<decimal> ReverseArForReturnAsync(SalesReturn doc)
        {
            if (_arInvoiceService == null) return 0m;
            var lines = doc.Lines.Count > 0
                ? doc.Lines.ToList()
                : await _context.SalesReturnLines.Where(l => l.SalesReturnId == doc.SalesReturnId).ToListAsync();
            decimal gap = 0m;
            foreach (var l in lines)
                if (!string.IsNullOrEmpty(l.GoodsShipmentId) && l.ShipmentLineNo.HasValue && l.ReturnQty > 0m)
                    gap += (await _arInvoiceService.ReverseForReturnedQtyAsync(
                        l.GoodsShipmentId!, l.ShipmentLineNo.Value, l.ReturnQty)).UnreversedGrossLocal;

            // 有缺口就當場開一張折讓單草稿（W3-F3 Phase 2）。
            // 原本這裡只是把金額寫進訊息裡叫人「另開折讓單」，而系統裡並沒有那種單據——
            // 稽核擋著關帳、畫面上卻沒有任何地方處理得掉它。
            // 產生器本身冪等（唯一索引 + 前置查詢），重試或多入口重複呼叫只會有一張。
            if (gap > 0m && _creditNoteService != null)
                _lastCreditNoteId =
                    (await _creditNoteService.CreateDraftFromSalesReturnAsync(doc.SalesReturnId))?.ARCreditNoteId;
            return gap;
        }

        /// <summary>
        /// 作廢銷退的前置條件：沒有未作廢的折讓單、來源出貨的應收發票都已作廢（或根本沒開）。
        /// 回傳擋下的訊息；可作廢時回 null。
        /// </summary>
        private async Task<string?> ReturnVoidBlockerAsync(SalesReturn doc)
        {
            var note = await _context.ARCreditNotes.AsNoTracking()
                .Where(n => n.SourceDocType == "SalesReturn" && n.SourceDocId == doc.SalesReturnId
                         && n.Status != Modules.FIN.Models.CreditNoteStatus.Void)
                .Select(n => n.ARCreditNoteId).FirstOrDefaultAsync();
            if (note != null)
                return string.Format(_localizer["ReturnVoidNoteFirst"].Value, note);
            var shipIds = doc.Lines.Where(l => !string.IsNullOrEmpty(l.GoodsShipmentId)).Select(l => l.GoodsShipmentId!).Distinct().ToList();
            var invoice = await _context.ARInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsShipment" && shipIds.Contains(i.SourceDocId!)
                         && i.Status != Modules.FIN.Models.ARInvoiceStatus.Void)
                .Select(i => i.ARInvoiceId).FirstOrDefaultAsync();
            return invoice == null ? null : string.Format(_localizer["SalesReturnVoidInvoiceLive"].Value, invoice);
        }

        /// <summary>剛剛產生（或既有）的折讓單號，供成功訊息指路。</summary>
        private string? _lastCreditNoteId;

        /// <summary>把未回沖缺口與折讓單號接在成功訊息後面。缺口 0 時原訊息原樣返回。</summary>
        private string AppendArGapWarning(string message, decimal gap)
            => gap <= 0m
                ? message
                : message + " " + string.Format(
                    _localizer["ArNotReversedAfterSettlement"].Value,
                    gap.ToString("N2"), _lastCreditNoteId ?? "-");

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        /// <summary>後端權威覆寫 vm.Lines 的 ExchangeRate（賣出匯率，找不到時保留原值）。</summary>
        private async Task ResolveLineRatesAsync(IEnumerable<SalesReturnLineVm> lines, DateOnly documentDate)
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
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;
            ViewBag.SearchTerm = searchTerm; ViewBag.CompanyCode = companyCode;
            ViewBag.DateFrom = dateFrom; ViewBag.DateTo = dateTo;
            ViewBag.Searched = searched; ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            await PrepareIndexPermissionsAsync(MenuKey);
            ViewBag.BaseCurrencyDp  = await ResolveBaseCurrencyDpAsync();
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();
            if (!searched) return View((PaginatedList<SalesReturn>?)null);

            var query = _context.SalesReturns.AsNoTracking()
                .Include(d => d.Lines).Where(d => d.PlantId == plantId);
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.SalesReturnId.Contains(searchTerm) || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(companyCode)) query = query.Where(d => d.CompanyCode == companyCode);
            if (dateFrom.HasValue) query = query.Where(d => d.ReturnDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.ReturnDate <= dateTo.Value);
            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(d => d.SalesReturnId),
                "id_desc"   => query.OrderByDescending(d => d.SalesReturnId),
                "date_asc"  => query.OrderBy(d => d.ReturnDate),
                "date_desc" => query.OrderByDescending(d => d.ReturnDate),
                _           => query.OrderByDescending(d => d.SalesReturnId),
            };
            return View(await PaginatedList<SalesReturn>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new SalesReturnFormVm { CreatedBy = _userContext.UserId ?? "", ReturnDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return NotFound();
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns(); ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", MapToVm(doc)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(SalesReturnFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.SalesReturnId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId = _userContext.UserId ?? ""; var plantId = _userContext.PlantId ?? "";
                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                // 後端權威覆寫 ExchangeRate（賣出匯率）
                await ResolveLineRatesAsync(vm.Lines, vm.ReturnDate);

                if (string.IsNullOrEmpty(vm.SalesReturnId))
                {
                    var doc = new SalesReturn
                    {
                        SalesReturnId = await _docNumberService.NextAsync(DocType, plantId, vm.ReturnDate),
                        ReturnDate = vm.ReturnDate, TypeCode = "GR",
                        CompanyCode = vm.CompanyCode, GoodsShipmentId = vm.GoodsShipmentId,
                        Remarks = vm.Remarks, CreatedBy = userId,
                        ApprovalStatus = targetStatus,
                    };
                    await ApplyLinesAsync(doc, vm); _context.SalesReturns.Add(doc);
                    await UpdateGsReturnedQtyAsync(doc);
                }
                else
                {
                    var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == vm.SalesReturnId)
                        ?? throw new InvalidOperationException(PzText.Get("Ord_SalesReturnNotFound"));
                    // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue（見 ord-module.md §13）
                    _context.Entry(doc).Property(e => e.xmin).OriginalValue = vm.RowVersion;
                    if (doc.Status != DocStatus.Draft) throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftEditable"));
                    await ReverseGsReturnedQtyAsync(doc);
                    doc.ReturnDate = vm.ReturnDate; doc.CompanyCode = vm.CompanyCode;
                    doc.GoodsShipmentId = vm.GoodsShipmentId; doc.Remarks = vm.Remarks; doc.UpdatedAt = DateTime.UtcNow;
                    doc.ApprovalStatus = targetStatus;
                    _context.SalesReturnLines.RemoveRange(doc.Lines); doc.Lines.Clear(); await ApplyLinesAsync(doc, vm);
                    await UpdateGsReturnedQtyAsync(doc);
                }
                await _context.SaveChangesAsync();
                return Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                var latest = await _context.SalesReturns.AsNoTracking().Include(d => d.Lines)
                    .FirstOrDefaultAsync(d => d.SalesReturnId == vm.SalesReturnId);
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
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return NotFound();
            if (doc.Status == DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["AlreadyPostedCannotRepost"].Value });
            if (doc.Status == DocStatus.Void)   return BadRequest(new { success = false, message = _localizer["AlreadyVoidedCannotConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any()) return BadRequest(new { success = false, message = _localizer["AddLineFirst"].Value });

            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);

            if (simplified)
            {
                // 過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住過帳與單據狀態更新。
                await using var tx = await _context.Database.BeginTransactionAsync();
                try
                {
                    var lines = await BuildSimplifiedLinesAsync(doc);
                    await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                    {
                        PlantId      = doc.PlantId ?? "",
                        DocNo        = doc.SalesReturnId,
                        MovementDate = doc.ReturnDate,
                        TypeCode     = "GR",
                        TypeCategory = "Sales",
                        CreatedBy    = _userContext.UserId,
                        Lines        = lines,
                        SetDocPosted = () => doc.Status = DocStatus.Posted,
                    });
                    // 完成過帳後：負向回沖來源出貨的應收發票。
                    // 註：舊註解寫「僅子帳，對稱 AR 產生不入 GL」已不成立——開票補上分錄後，
                    // 回沖也會產生反向分錄（借 銷貨收入＋銷項稅／貸 應收帳款）。
                    // 這一行與上面的過帳同在一個交易內，三腿（庫存、COGS、AR）要嘛全成要嘛全退，
                    // 由 Flows/SalesReturnFlowTests 守住。
                    var arGap = await ReverseArForReturnAsync(doc);
                    await _context.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Ok(new { success = true,
                                    message = AppendArGapWarning(_localizer["PostedSimple"].Value, arGap) });
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

            doc.Status = DocStatus.Confirmed;

            // ── RQC 觸發（Phase G-H）：依 InspectionSpec.RequiresRQC 自動建立待檢單 ──
            await CreateRqcOrdersAsync(doc);

            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["ConfirmedAwaitingPutaway"].Value });
        }

        /// <summary>
        /// 對需要 RQC 的品項自動建立 InspectionOrder(Type=RQC, Status=Pending)。
        /// 由 InspectionSpec.RequiresRQC 控制；同一銷退單 + 品號若已有 Pending 待檢則不重複建。
        /// 結果不影響確認流程（非阻斷）。
        /// </summary>
        private async Task CreateRqcOrdersAsync(SalesReturn doc)
        {
            var plantId = doc.PlantId ?? _userContext.PlantId ?? "";
            var now     = DateTime.UtcNow;

            // 已建立的 RQC 待檢品號集合（避免重複）
            var existingRqcList = await _context.InspectionOrders
                .Where(o => o.InspectionType == "RQC" && o.SourceDocId == doc.SalesReturnId)
                .Select(o => o.ProductCode)
                .ToListAsync();
            var existingRqc = existingRqcList.ToHashSet();

            // 查出此廠別所有 RequiresRQC = true 的規格（快取）
            var rqcSpecs = await _context.InspectionSpecs
                .AsNoTracking()
                .Where(s => s.PlantId == plantId && s.IsActive && s.RequiresRQC)
                .ToListAsync();

            foreach (var line in doc.Lines)
            {
                if (existingRqc.Contains(line.ProductCode)) continue;

                var spec = rqcSpecs.FirstOrDefault(s => s.ProductCode == line.ProductCode)
                        ?? rqcSpecs.FirstOrDefault(s => s.ProductCode == null);

                if (spec == null) continue;

                var decision = await _sampling.GetDecisionAsync(
                    line.ProductCode, supplierCode: null, "RQC",
                    line.ReturnQty, plantId);

                if (!decision.ShouldInspect) continue;  // 跳批

                _context.InspectionOrders.Add(new SmartFactory.MES.Models.InspectionOrder
                {
                    InspectionType    = "RQC",
                    PlantId           = plantId,
                    SourceDocId       = doc.SalesReturnId,
                    SourceLineNo      = line.LineNo,
                    ProductCode       = line.ProductCode,
                    LotSize           = line.ReturnQty,
                    SampleSize        = decision.SampleSize,
                    VerificationLevel = decision.VerificationLevel,
                    Status            = "Pending",
                    InspectorId       = _userContext.UserId,
                    CreatedAt         = now,
                    UpdatedAt         = now
                });

                existingRqc.Add(line.ProductCode);
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Confirmed) return BadRequest(new { success = false, message = _localizer["ConfirmedOnlyCanUnconfirm"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0)) return BadRequest(new { success = false, message = _localizer["HasPutawayCannotUnconfirm"].Value });
            doc.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            // 退貨過帳時已回沖應收（未收款發票直接改小、已收款開折讓單），作廢卻只沖庫存與成本：
            // 貨又出去了，應收與收入永久少記（2026-10-22 ReceivableLedgerModelTests 發現）。
            // 自動補回要另外記「哪張退貨扣了多少」且可能動到已結清發票，先擋下，待定案。
            var blocker = await ReturnVoidBlockerAsync(doc);
            if (blocker != null) return BadRequest(new { success = false, message = blocker });
            // 作廢沖銷過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住沖銷與單據狀態更新。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = doc.PlantId ?? "", DocNo = doc.SalesReturnId,
                    Reason = reason, CreatedBy = _userContext.UserId,
                    SetDocVoid = () => doc.Status = DocStatus.Void,
                });
                // 作廢回沖出貨明細累計退回量（對齊出貨單作廢回沖 ShippedQty 的語意）
                await ReverseGsReturnedQtyAsync(doc);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException)
            { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<SalesReturn>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<SalesReturn>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var doc = await _context.SalesReturns.Include(d => d.Lines).FirstOrDefaultAsync(d => d.SalesReturnId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Status != DocStatus.Draft) return Json(new { success = false, message = _localizer["StagingErrDraftToDelete"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasPutawayCannotDelete"].Value });
            await ReverseGsReturnedQtyAsync(doc);
            _context.SalesReturns.Remove(doc); await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> AvailableLines(string? searchTerm, string? companyCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 SR 明細加總已退貨量（排除作廢）
            var returnedRaw = await _context.SalesReturnLines.AsNoTracking()
                .Where(rl => rl.SalesReturn!.PlantId == plantId
                    && rl.SalesReturn.Status != DocStatus.Void
                    && rl.GoodsShipmentId != null && rl.ShipmentLineNo.HasValue)
                .GroupBy(rl => new { rl.GoodsShipmentId, rl.ShipmentLineNo })
                .Select(g => new { g.Key.GoodsShipmentId, LineNo = g.Key.ShipmentLineNo!.Value, Returned = g.Sum(rl => rl.ReturnQty) })
                .ToListAsync();
            var returnedMap = returnedRaw.ToDictionary(x => (x.GoodsShipmentId!, x.LineNo), x => x.Returned);

            var query = _context.GoodsShipmentLines.AsNoTracking().Include(l => l.GoodsShipment)
                .Where(l => l.GoodsShipment!.PlantId == plantId && l.GoodsShipment.Status == DocStatus.Posted);
            if (!string.IsNullOrEmpty(companyCode)) query = query.Where(l => l.GoodsShipment!.CompanyCode == companyCode);
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(l => l.GoodsShipmentId.ToLower().Contains(lower) || l.ProductCode.ToLower().Contains(lower));
            }
            var gsLines = await query.OrderBy(l => l.GoodsShipmentId).ThenBy(l => l.LineNo)
                .Select(l => new
                {
                    l.GoodsShipmentId, l.LineNo, l.ProductCode,
                    l.ShipQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                }).Take(200).ToListAsync();

            var lines = gsLines
                .Select(l =>
                {
                    var returned  = returnedMap.TryGetValue((l.GoodsShipmentId, l.LineNo), out var r) ? r : 0m;
                    var remaining = l.ShipQty - returned;
                    return new
                    {
                        l.GoodsShipmentId, l.LineNo, l.ProductCode,
                        l.ShipQty, l.UomCode, l.StockQty, l.StockUomCode,
                        l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                        ReturnedQty = returned, RemainingQty = remaining,
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
            var doc = await _context.SalesReturns.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.SalesReturnId == id && d.PlantId == _userContext.PlantId);
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

        private static SalesReturnFormVm MapToVm(SalesReturn doc) => new()
        {
            SalesReturnId = doc.SalesReturnId, ApprovalStatus = doc.ApprovalStatus, Status = doc.Status, RowVersion = doc.xmin,
            ReturnDate = doc.ReturnDate, CreatedBy = doc.CreatedBy, CompanyCode = doc.CompanyCode,
            GoodsShipmentId = doc.GoodsShipmentId, Remarks = doc.Remarks,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new SalesReturnLineVm
            {
                LineNo = l.LineNo, GoodsShipmentId = l.GoodsShipmentId, ShipmentLineNo = l.ShipmentLineNo,
                ProductCode = l.ProductCode, ReturnQty = l.ReturnQty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice, CurrencyCode = l.CurrencyCode, ExchangeRate = l.ExchangeRate,
                TaxCode = l.TaxCode, TaxRate = l.TaxRate,
                LineAmount = l.LineAmount, TaxAmount = l.TaxAmount, TotalAmount = l.TotalAmount, AmountLocal = l.AmountLocal,
                QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
            }).ToList()
        };

        private async Task ApplyLinesAsync(SalesReturn doc, SalesReturnFormVm vm)
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
                    ? lv.ReturnQty
                    : await _uomService.ConvertAsync(lv.ProductCode, lv.UomCode!, stockUom, lv.ReturnQty);
                stockQty = await _uomService.RoundQtyAsync(stockUom, stockQty);

                doc.Lines.Add(new SalesReturnLine
                {
                    SalesReturnId = doc.SalesReturnId, LineNo = seq++,
                    GoodsShipmentId = lv.GoodsShipmentId, ShipmentLineNo = lv.ShipmentLineNo,
                    ProductCode = lv.ProductCode, ReturnQty = lv.ReturnQty, UomCode = lv.UomCode,
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

        private async Task<List<SimplifiedPostLine>> BuildSimplifiedLinesAsync(SalesReturn doc)
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

                // 入庫成本取原出貨實際扣掉的單位成本，不是銷退單上的售價；
                // 找不到來源時回 null，由過帳端以現行單位成本入庫（見 SalesReturnCostBasis）。
                var unitCost = await Services.SalesReturnCostBasis.ResolveUnitCostAsync(
                    _context, line.GoodsShipmentId, line.ShipmentLineNo);

                var currentLine = line;
                result.Add(new SimplifiedPostLine
                {
                    LineNo        = line.LineNo,
                    ProductCode   = line.ProductCode,
                    LocationCode  = location,
                    WhCode        = whCode ?? "",
                    LotNo         = line.LotNo ?? "",
                    Qty           = line.StockQty,
                    BusinessQty   = line.ReturnQty,
                    UomCode       = line.UomCode ?? "",
                    StockUomCode  = line.StockUomCode ?? "",
                    UnitPrice     = unitCost,
                    SourceDocId   = line.GoodsShipmentId,
                    SourceLineNo  = line.ShipmentLineNo,
                    SetQtyPutAway = qty => currentLine.QtyPutAway = qty,
                });
            }
            return result;
        }

        private async Task UpdateGsReturnedQtyAsync(SalesReturn doc)
        {
            foreach (var line in doc.Lines.Where(l => !string.IsNullOrEmpty(l.GoodsShipmentId) && l.ShipmentLineNo.HasValue))
            {
                var gsLine = await _context.GoodsShipmentLines
                    .FindAsync(line.GoodsShipmentId, line.ShipmentLineNo!.Value);
                if (gsLine == null) continue;
                // 上限＝出貨量：退回不得多於出去的。沒有這道守門，可退量（ShipQty − ReturnedQty）
                // 會變成負數，而負的可退量沒有任何地方會擋，等於一張出貨可以無限退。
                // 退回還會負向回沖來源 AR 發票，超退直接把應收沖成小於實際應收。
                if (gsLine.ReturnedQty + line.ReturnQty > gsLine.ShipQty)
                    throw new InvalidOperationException(
                        PzText.Get("Ord_ReturnExceedsShipFmt", line.GoodsShipmentId, line.ShipmentLineNo, gsLine.ReturnedQty + line.ReturnQty, gsLine.ShipQty));
                gsLine.ReturnedQty += line.ReturnQty;
            }
        }

        private async Task ReverseGsReturnedQtyAsync(SalesReturn doc)
        {
            foreach (var line in doc.Lines.Where(l => !string.IsNullOrEmpty(l.GoodsShipmentId) && l.ShipmentLineNo.HasValue))
            {
                var gsLine = await _context.GoodsShipmentLines
                    .FindAsync(line.GoodsShipmentId, line.ShipmentLineNo!.Value);
                // 下限 0：已退貨量是「累計」語意。負的已退量會讓這張出貨的可退量
                // 算得比出貨量還多（ShipQty − ReturnedQty），憑空多出可退額度。
                // 與 PurchaseService 回沖 GoodsReceiptLine.ReturnedQty 同一寫法。
                if (gsLine != null) gsLine.ReturnedQty = Math.Max(0m, gsLine.ReturnedQty - line.ReturnQty);
            }
        }

    }
}
