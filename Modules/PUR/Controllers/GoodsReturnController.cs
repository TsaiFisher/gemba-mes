using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    [Authorize]
    public class GoodsReturnController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPurchaseService _purchaseService;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IPlantParamService _plantParams;
        private readonly IInventoryPostingService _postingService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.FIN.Services.IAPInvoiceService? _apInvoiceService;   // optional：FIN 未啟用時為 null
        private readonly Modules.FIN.Services.IAPDebitNoteService? _debitNoteService;  // 同上

        private const string MenuKey = "GoodsReturn";
        private const string DocType = "GoodsReturn";

        public GoodsReturnController(
            ApplicationDbContext context,
            IPurchaseService purchaseService,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IPlantParamService plantParams,
            IInventoryPostingService postingService,
            IUomService uomService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.FIN.Services.IAPInvoiceService? apInvoiceService = null,
            Modules.FIN.Services.IAPDebitNoteService? debitNoteService = null)
        {
            _context = context;
            _purchaseService = purchaseService;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _approvalService = approvalService;
            _plantParams = plantParams;
            _postingService = postingService;
            _uomService = uomService;
            _currencyService = currencyService;
            _apInvoiceService = apInvoiceService;
            _debitNoteService = debitNoteService;
            _localizer = localizer;
        }

        /// <summary>
        /// 進貨退出「完成（過帳）」後置：以退貨量負向回沖來源進貨單的應付發票（降低應付）。
        /// 只處理有連結來源 GR 明細（ReceiptLineNo）的退貨行；ReturnQty 與 AP 認列同為進貨單位。
        ///
        /// 回傳**沒有回沖掉**的金額合計（本位幣含稅）：來源發票已付款時服務層不動它，
        /// 但那筆錢廠商實際上欠公司。原本這裡把回傳值丟掉，缺口因此完全無聲。
        /// </summary>
        private async Task<decimal> ReverseApForReturnAsync(GoodsReturn grt)
        {
            if (_apInvoiceService == null) return 0m;
            var lines = grt.Lines.Count > 0
                ? grt.Lines.ToList()
                : await _context.GoodsReturnLines.Where(l => l.GoodsReturnId == grt.GoodsReturnId).ToListAsync();
            decimal gap = 0m;
            foreach (var l in lines)
                if (!string.IsNullOrEmpty(l.GoodsReceiptId) && l.ReceiptLineNo.HasValue && l.ReturnQty > 0m)
                    gap += (await _apInvoiceService.ReverseForReturnedQtyAsync(
                        l.GoodsReceiptId!, l.ReceiptLineNo.Value, l.ReturnQty)).UnreversedGrossLocal;

            // 有缺口就當場開一張折讓單草稿（W3-F3 Phase 2，與 AR 側同一個修法）。
            // 產生器冪等，重試或多入口重複呼叫只會有一張。
            if (gap > 0m && _debitNoteService != null)
                _lastDebitNoteId =
                    (await _debitNoteService.CreateDraftFromGoodsReturnAsync(grt.GoodsReturnId))?.APDebitNoteId;
            return gap;
        }

        /// <summary>作廢進貨退出的前置條件：沒有未作廢的折讓單、來源進貨的應付發票都已作廢（或根本沒認列）。</summary>
        private async Task<string?> ReturnVoidBlockerAsync(GoodsReturn grt)
        {
            var note = await _context.APDebitNotes.AsNoTracking()
                .Where(n => n.SourceDocType == "GoodsReturn" && n.SourceDocId == grt.GoodsReturnId
                         && n.Status != Modules.FIN.Models.CreditNoteStatus.Void)
                .Select(n => n.APDebitNoteId).FirstOrDefaultAsync();
            if (note != null)
                return string.Format(_localizer["ReturnVoidNoteFirst"].Value, note);
            var grIds = grt.Lines.Where(l => !string.IsNullOrEmpty(l.GoodsReceiptId)).Select(l => l.GoodsReceiptId!).Distinct().ToList();
            var invoice = await _context.APInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsReceipt" && grIds.Contains(i.SourceDocId!)
                         && i.Status != Modules.FIN.Models.APInvoiceStatus.Void)
                .Select(i => i.APInvoiceId).FirstOrDefaultAsync();
            return invoice == null ? null : string.Format(_localizer["GoodsReturnVoidInvoiceLive"].Value, invoice);
        }

        /// <summary>剛剛產生（或既有）的折讓單號，供成功訊息指路。</summary>
        private string? _lastDebitNoteId;

        /// <summary>把未回沖缺口與折讓單號接在成功訊息後面。缺口 0 時原訊息原樣返回。</summary>
        private string AppendApGapWarning(string message, decimal gap)
            => gap <= 0m
                ? message
                : message + " " + string.Format(
                    _localizer["ApNotReversedAfterSettlement"].Value,
                    gap.ToString("N2"), _lastDebitNoteId ?? "-");

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            ViewBag.ProductOptions = await _context.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName, p.PurchaseUom, p.StockUom })
                .ToListAsync();

            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            ViewBag.CurrencyOptions = await _context.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName })
                .ToListAsync();

            ViewBag.TaxOptions = await _context.Taxes
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.TaxCode)
                .Select(t => new { t.TaxCode, t.TaxName, t.TaxRate })
                .ToListAsync();

            ViewBag.WarehouseOptions = await _context.Warehouses
                .AsNoTracking()
                .Where(w => w.IsActive)
                .OrderBy(w => w.WhCode)
                .Select(w => new { w.WhCode, w.WhName })
                .ToListAsync();

            ViewBag.TransactionTypeOptions = await _context.InventoryTransactionTypes
                .AsNoTracking()
                .Where(t => t.IsActive && t.Direction == -1)
                .OrderBy(t => t.TypeCode)
                .Select(t => new { t.TypeCode, t.TypeName })
                .ToListAsync();

            ViewBag.UomDecimals      = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            // 明細本幣金額（AmountLocal）以本位幣代號與精度顯示、加總
            ViewBag.BaseCurrency     = await _currencyService.GetBaseCurrencyAsync() ?? "";
            ViewBag.BaseCurrencyDp   = await ResolveBaseCurrencyDpAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        // GET: /GoodsReturn
        public async Task<IActionResult> Index(
            string? searchTerm, string? supplierCode,
            DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;

            ViewBag.SearchTerm      = searchTerm;
            ViewBag.SupplierCode    = supplierCode;
            ViewBag.DateFrom        = dateFrom;
            ViewBag.DateTo          = dateTo;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.IdSort          = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            if (!searched) return View((PaginatedList<GoodsReturn>?)null);

            var query = _context.GoodsReturns
                .AsNoTracking()
                .Include(grt => grt.Lines)
                .Where(grt => grt.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(grt => grt.GoodsReturnId.Contains(searchTerm) || grt.Remarks!.Contains(searchTerm));

            if (!string.IsNullOrWhiteSpace(supplierCode))
                query = query.Where(grt => grt.SupplierCode == supplierCode);

            if (dateFrom.HasValue)
                query = query.Where(grt => grt.ReturnDate >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(grt => grt.ReturnDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(grt => grt.GoodsReturnId),
                "id_desc"   => query.OrderByDescending(grt => grt.GoodsReturnId),
                "date_asc"  => query.OrderBy(grt => grt.ReturnDate),
                "date_desc" => query.OrderByDescending(grt => grt.ReturnDate),
                _            => query.OrderByDescending(grt => grt.GoodsReturnId),
            };

            var paged = await PaginatedList<GoodsReturn>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        // GET: /GoodsReturn/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new GoodsReturnFormVm { CreatedBy = _userContext.UserId ?? "", ReturnDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /GoodsReturn/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

            var grt = await _context.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
            if (grt == null) return NotFound();

            await SyncApprovalStatusAsync(grt, () => OnGrtApprovedAsync(grt));

            await PrepareFormDropdowns();

            var vm = new GoodsReturnFormVm
            {
                GoodsReturnId  = grt.GoodsReturnId,
                ApprovalStatus = grt.ApprovalStatus,
                RowVersion     = grt.xmin,
                Status         = grt.Status,
                ReturnDate     = grt.ReturnDate,
                CreatedBy      = grt.CreatedBy,
                SupplierCode   = grt.SupplierCode,
                Remarks        = grt.Remarks,
                Lines = grt.Lines.OrderBy(l => l.LineNo).Select(l => new GoodsReturnLineVm
                {
                    LineNo          = l.LineNo,
                    GoodsReceiptId  = l.GoodsReceiptId,
                    ReceiptLineNo   = l.ReceiptLineNo,
                    ProductCode     = l.ProductCode,
                    ReturnQty       = l.ReturnQty,
                    UomCode         = l.UomCode,
                    StockQty        = l.StockQty,
                    StockUomCode    = l.StockUomCode,
                    UnitPrice       = l.UnitPrice,
                    CurrencyCode    = l.CurrencyCode,
                    ExchangeRate    = l.ExchangeRate,
                    TaxCode         = l.TaxCode,
                    TaxRate         = l.TaxRate,
                    LineAmount      = l.LineAmount,
                    TaxAmount       = l.TaxAmount,
                    TotalAmount     = l.TotalAmount,
                    AmountLocal     = l.AmountLocal,
                    WhCode          = l.WhCode ?? "",
                    LocationCode    = l.LocationCode,
                    LotNo           = l.LotNo,
                    Remarks         = l.Remarks,
                    TypeCode        = "PUR-RTN",
                    QtyPutAway      = l.QtyPutAway,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /GoodsReturn/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var grt = await _context.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
            if (grt == null) return NotFound();

            await SyncApprovalStatusAsync(grt, () => OnGrtApprovedAsync(grt));

            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;

            var vm = new GoodsReturnFormVm
            {
                GoodsReturnId  = grt.GoodsReturnId,
                ApprovalStatus = grt.ApprovalStatus,
                RowVersion     = grt.xmin,
                Status         = grt.Status,
                ReturnDate     = grt.ReturnDate,
                CreatedBy      = grt.CreatedBy,
                SupplierCode   = grt.SupplierCode,
                Remarks        = grt.Remarks,
                Lines = grt.Lines.OrderBy(l => l.LineNo).Select(l => new GoodsReturnLineVm
                {
                    LineNo          = l.LineNo,
                    GoodsReceiptId  = l.GoodsReceiptId,
                    ReceiptLineNo   = l.ReceiptLineNo,
                    ProductCode     = l.ProductCode,
                    ReturnQty       = l.ReturnQty,
                    UomCode         = l.UomCode,
                    StockQty        = l.StockQty,
                    StockUomCode    = l.StockUomCode,
                    UnitPrice       = l.UnitPrice,
                    CurrencyCode    = l.CurrencyCode,
                    ExchangeRate    = l.ExchangeRate,
                    TaxCode         = l.TaxCode,
                    TaxRate         = l.TaxRate,
                    LineAmount      = l.LineAmount,
                    TaxAmount       = l.TaxAmount,
                    TotalAmount     = l.TotalAmount,
                    AmountLocal     = l.AmountLocal,
                    WhCode          = l.WhCode ?? "",
                    LocationCode    = l.LocationCode,
                    LotNo           = l.LotNo,
                    Remarks         = l.Remarks,
                    TypeCode        = "PUR-RTN",
                    QtyPutAway      = l.QtyPutAway,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // POST: /GoodsReturn/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(GoodsReturnFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.GoodsReturnId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<GoodsReturn>(vm.GoodsReturnId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns();
                    return PartialView("_Form", vm);
                }

                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                // 建/改單→（簡易模式）過帳→存檔為單一原子單元：過帳路徑內含日記帳自身
                // SaveChanges，須以顯式交易包住，避免中途失敗造成單據與庫存/分錄不一致。
                await using var tx = await _context.Database.BeginTransactionAsync();

                var isNew = string.IsNullOrEmpty(vm.GoodsReturnId);
                GoodsReturn grt;

                if (isNew)
                {
                    grt = await _purchaseService.CreateGoodsReturnAsync(vm, userId, plantId);
                }
                else
                {
                    // 鎖定檢查
                    var existing = await _context.GoodsReturns.AsNoTracking().Include(g => g.Lines)
                        .FirstOrDefaultAsync(g => g.GoodsReturnId == vm.GoodsReturnId);
                    if (existing != null && existing.Lines.Any(l => l.QtyPutAway > 0))
                    {
                        throw new InvalidOperationException(PzText.Get("Pur_WarehouseWorkStarted"));
                    }
                    // xmin 樂觀鎖的蓋章在 UpdateGoodsReturnAsync 內部、寫入之前完成
                    grt = await _purchaseService.UpdateGoodsReturnAsync(vm, userId);
                }

                grt.ApprovalStatus = targetStatus;

                // 自動轉 Confirmed / Posted
                if (targetStatus == DocumentApprovalStatus.Approved)
                {
                    var simplified = string.Equals(
                        await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                        "true", StringComparison.OrdinalIgnoreCase);

                    if (simplified && grt.Status != DocStatus.Posted)
                    {
                        var simplifiedLines = await BuildSimplifiedLinesAsync(grt);
                        await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                        {
                            PlantId = grt.PlantId ?? "",
                            DocNo = grt.GoodsReturnId,
                            MovementDate = grt.ReturnDate,
                            TypeCode = grt.TypeCode,
                            TypeCategory = grt.TypeCategory,
                            CreatedBy = userId,
                            Lines = simplifiedLines,
                            SetDocPosted = () => grt.Status = DocStatus.Posted,
                        });
                    }
                    else if (!simplified)
                    {
                        grt.Status = DocStatus.Confirmed;
                    }
                }

                await _context.SaveChangesAsync();

                // 簡化模式：完成即過帳、貨已退出正常倉 → 就地負向回沖來源進貨單的應付。
                // 完整模式（Confirmed）：貨待下架，於下架（WarehouseApiController pick）時才回沖。
                decimal apGap = 0m;
                if (grt.Status == DocStatus.Posted)
                    apGap = await ReverseApForReturnAsync(grt);

                await tx.CommitAsync();

                // 這條是回傳 <script> 的舊路徑，沒有 message 欄位可接；缺口用 alert 讓人一定看到。
                // JsonSerializer.Serialize 產生的是合法的 JS 字串常值，訊息含引號也不會把腳本弄壞。
                // 訊息與 Confirm 共用 AppendApGapWarning：資源字串有兩個參數（金額、折讓單號），
                // 原本這裡只給金額，string.Format 擲 FormatException——而交易已在上面提交，
                // 畫面報錯、退出與折讓草稿卻已成立，使用者重按就退兩次（2026-10-22 隨機序列測試發現）。
                var gapScript = apGap > 0m
                    ? "alert(" + System.Text.Json.JsonSerializer.Serialize(AppendApGapWarning("", apGap).TrimStart()) + ");"
                    : "";
                return Content("<script>window.toastSuccess && window.toastSuccess(); "
                             + gapScript + "refreshList && refreshList();</script>",
                    "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
        }

        // POST: /GoodsReturn/Submit/{id} — 送審
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<GoodsReturn>(id, MenuKey, DocType);

        // POST: /GoodsReturn/Withdraw/{id} — 撤回
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<GoodsReturn>(id, MenuKey);

        private async Task OnGrtApprovedAsync(GoodsReturn grt)
        {
            if (grt.Status != DocStatus.Draft) return;
            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);
            if (simplified)
            {
                // 自動過帳例外不在此吞掉：交由 SyncApprovalStatusAsync 統一以交易回滾並提示。
                var simplifiedLines = await BuildSimplifiedLinesAsync(grt);
                await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = grt.PlantId ?? "",
                    DocNo = grt.GoodsReturnId,
                    MovementDate = grt.ReturnDate,
                    TypeCode = grt.TypeCode,
                    TypeCategory = grt.TypeCategory,
                    CreatedBy = _userContext.UserId,
                    Lines = simplifiedLines,
                    SetDocPosted = () => grt.Status = DocStatus.Posted,
                });
            }
            else grt.Status = DocStatus.Confirmed;
        }

        // POST: /GoodsReturn/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            try
            {
                var doc = await _context.GoodsReturns.AsNoTracking().Include(g => g.Lines)
                    .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
                if (doc != null && doc.Lines.Any(l => l.QtyPutAway > 0))
                    return BadRequest(new { success = false, message = _localizer["HasWarehouseOpsCannotDelete"].Value });

                await _purchaseService.DeleteGoodsReturnAsync(id);
                return Ok(new { success = true, message = _localizer["DeleteSuccess"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // GET: /GoodsReturn/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.GoodsReturns
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.GoodsReturnId == id && d.PlantId == _userContext.PlantId);
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

        // POST: /GoodsReturn/Confirm/{id} — Draft → Confirmed（或簡化模式下 → Posted）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var grt = await _context.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
            if (grt == null) return NotFound();

            if (grt.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (grt.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!grt.Lines.Any())
                return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);

            if (simplified)
            {
                // 過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住過帳與單據狀態更新。
                await using var tx = await _context.Database.BeginTransactionAsync();
                try
                {
                    var simplifiedLines = await BuildSimplifiedLinesAsync(grt);
                    await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                    {
                        PlantId = grt.PlantId ?? "",
                        DocNo = grt.GoodsReturnId,
                        MovementDate = grt.ReturnDate,
                        TypeCode = grt.TypeCode,
                        TypeCategory = grt.TypeCategory,
                        CreatedBy = _userContext.UserId,
                        Lines = simplifiedLines,
                        SetDocPosted = () => grt.Status = DocStatus.Posted,
                    });
                    await _context.SaveChangesAsync();
                    // 簡化模式確認即過帳、貨已退出 → 就地負向回沖來源進貨單應付。
                    var apGap = await ReverseApForReturnAsync(grt);
                    await tx.CommitAsync();
                    return Ok(new { success = true,
                                    message = AppendApGapWarning(_localizer["PostedSimple"].Value, apGap) });
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

            grt.Status = DocStatus.Confirmed;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["ConfirmedAwaitingWarehouse"].Value });
        }

        // POST: /GoodsReturn/CancelConfirm/{id} — Confirmed → Draft（僅限無倉庫作業者）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var grt = await _context.GoodsReturns
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
            if (grt == null) return NotFound();

            if (grt.Status != DocStatus.Confirmed)
                return BadRequest(new { success = false, message = _localizer["ConfirmedOnlyCanWithdraw"].Value });
            if (grt.Lines.Any(l => l.QtyPutAway > 0))
                return BadRequest(new { success = false, message = _localizer["WarehouseStartedContactStaff"].Value });

            grt.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        // POST: /GoodsReturn/Void/{id} — Posted → Void（沖銷所有 StockMovement）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var grt = await _context.GoodsReturns.Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReturnId == id);
            if (grt == null) return NotFound();

            if (grt.Status != DocStatus.Posted)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });

            // 同銷退：退出過帳時已回沖應付，作廢卻只沖庫存，先擋下待定案（2026-10-22 PayableLedgerModelTests 發現）
            var blocker = await ReturnVoidBlockerAsync(grt);
            if (blocker != null) return BadRequest(new { success = false, message = blocker });

            // 作廢沖銷過帳路徑內含日記帳自身 SaveChanges，須以顯式交易包住沖銷與單據狀態更新。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = grt.PlantId ?? "",
                    DocNo = grt.GoodsReturnId,
                    Reason = reason,
                    CreatedBy = _userContext.UserId,
                    SetDocVoid = () => grt.Status = DocStatus.Void,
                });
                // 作廢回沖 GR 明細累計退貨量（否則 §3.1 有效收料被永久低估）
                await _purchaseService.RestoreGrReturnedQtyAsync(grt.Lines);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        /// <summary>簡化模式：將每個 Line 映射為 SimplifiedPostLine。LocationCode 缺省時取 ProductCategory.DefaultLocationCode。</summary>
        private async Task<List<SimplifiedPostLine>> BuildSimplifiedLinesAsync(GoodsReturn grt)
        {
            var productCodes = grt.Lines.Select(l => l.ProductCode).Distinct().ToList();
            var productToCategory = await _context.Products
                .AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.CategoryCode })
                .ToDictionaryAsync(p => p.ProductCode, p => p.CategoryCode);

            var categoryCodes = productToCategory.Values.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var categoryDefaults = await _context.ProductCategories
                .AsNoTracking()
                .Where(c => categoryCodes.Contains(c.CategoryCode))
                .Select(c => new { c.CategoryCode, c.DefaultWarehouseCode, c.DefaultLocationCode })
                .ToDictionaryAsync(c => c.CategoryCode!, c => c);

            var result = new List<SimplifiedPostLine>();
            foreach (var line in grt.Lines.OrderBy(l => l.LineNo))
            {
                var location = line.LocationCode;
                var whCode = line.WhCode;
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
                    throw new InvalidOperationException(PzText.Get("Pur_LineNoLocationFmt", line.LineNo, line.ProductCode));

                var currentLine = line;
                result.Add(new SimplifiedPostLine
                {
                    LineNo = line.LineNo,
                    ProductCode = line.ProductCode,
                    LocationCode = location,
                    WhCode = whCode ?? "",
                    LotNo = line.LotNo ?? "",
                    Qty = line.StockQty,
                    BusinessQty = line.ReturnQty,
                    UomCode = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    UnitPrice = line.UnitPrice,
                    SetQtyPutAway = qty => currentLine.QtyPutAway = qty,
                });
            }
            return result;
        }
    }
}
