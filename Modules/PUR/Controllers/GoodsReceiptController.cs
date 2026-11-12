using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.MFG.Models;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    [Authorize]
    public class GoodsReceiptController : ModuleControllerBase
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
        private readonly IAPInvoiceService _apInvoiceService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "GoodsReceipt";
        private const string DocType = "GoodsReceipt";

        public GoodsReceiptController(
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
            IAPInvoiceService apInvoiceService,
            IStringLocalizer<ProjectZSharedResource> localizer)
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
                .Where(t => t.IsActive && t.Direction == 1)
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

        // GET: /GoodsReceipt
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

            if (!searched) return View((PaginatedList<GoodsReceipt>?)null);

            var query = _context.GoodsReceipts
                .AsNoTracking()
                .Include(gr => gr.Lines)
                .Where(gr => gr.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(gr => gr.GoodsReceiptId.Contains(searchTerm) || gr.Remarks!.Contains(searchTerm));

            if (!string.IsNullOrWhiteSpace(supplierCode))
                query = query.Where(gr => gr.SupplierCode == supplierCode);

            if (dateFrom.HasValue)
                query = query.Where(gr => gr.ReceiptDate >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(gr => gr.ReceiptDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(gr => gr.GoodsReceiptId),
                "id_desc"   => query.OrderByDescending(gr => gr.GoodsReceiptId),
                "date_asc"  => query.OrderBy(gr => gr.ReceiptDate),
                "date_desc" => query.OrderByDescending(gr => gr.ReceiptDate),
                _            => query.OrderByDescending(gr => gr.GoodsReceiptId),
            };

            var paged = await PaginatedList<GoodsReceipt>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        // GET: /GoodsReceipt/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new GoodsReceiptFormVm { CreatedBy = _userContext.UserId ?? "", ReceiptDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /GoodsReceipt/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

            var gr = await _context.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
            if (gr == null) return NotFound();

            await SyncApprovalStatusAsync(gr, () => OnGrApprovedAsync(gr));

            await PrepareFormDropdowns();

            var vm = new GoodsReceiptFormVm
            {
                GoodsReceiptId = gr.GoodsReceiptId,
                ApprovalStatus = gr.ApprovalStatus,
                RowVersion     = gr.xmin,
                Status         = gr.Status,
                ReceiptDate    = gr.ReceiptDate,
                CreatedBy      = gr.CreatedBy,
                SupplierCode   = gr.SupplierCode,
                Remarks        = gr.Remarks,
                Lines = gr.Lines.OrderBy(l => l.LineNo).Select(l => new GoodsReceiptLineVm
                {
                    LineNo          = l.LineNo,
                    PurchaseOrderId = l.PurchaseOrderId,
                    PoLineNo        = l.PoLineNo,
                    ProductCode     = l.ProductCode,
                    ReceiptQty      = l.ReceiptQty,
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
                    TypeCode        = "PUR-IN",
                    QtyPutAway      = l.QtyPutAway,
                    QtyGood         = l.QtyGood,
                    QtyRejected     = l.QtyRejected,
                }).ToList(),
                SourceDocType = gr.SourceDocType,
                SourceDocId   = gr.SourceDocId,
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /GoodsReceipt/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var gr = await _context.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
            if (gr == null) return NotFound();

            await SyncApprovalStatusAsync(gr, () => OnGrApprovedAsync(gr));

            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;

            var vm = new GoodsReceiptFormVm
            {
                GoodsReceiptId = gr.GoodsReceiptId,
                ApprovalStatus = gr.ApprovalStatus,
                RowVersion     = gr.xmin,
                Status         = gr.Status,
                ReceiptDate    = gr.ReceiptDate,
                CreatedBy      = gr.CreatedBy,
                SupplierCode   = gr.SupplierCode,
                Remarks        = gr.Remarks,
                Lines = gr.Lines.OrderBy(l => l.LineNo).Select(l => new GoodsReceiptLineVm
                {
                    LineNo          = l.LineNo,
                    PurchaseOrderId = l.PurchaseOrderId,
                    PoLineNo        = l.PoLineNo,
                    ProductCode     = l.ProductCode,
                    ReceiptQty      = l.ReceiptQty,
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
                    TypeCode        = "PUR-IN",
                    QtyPutAway      = l.QtyPutAway,
                }).ToList()
            };

            // Phase G: 補入委外來源欄位
            vm.SourceDocType = gr.SourceDocType;
            vm.SourceDocId   = gr.SourceDocId;
            foreach (var (vmLine, grLine) in vm.Lines.Zip(gr.Lines.OrderBy(l => l.LineNo)))
            {
                vmLine.QtyGood     = grLine.QtyGood;
                vmLine.QtyRejected = grLine.QtyRejected;
            }

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // POST: /GoodsReceipt/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(GoodsReceiptFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.GoodsReceiptId)
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

                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                var isNew = string.IsNullOrEmpty(vm.GoodsReceiptId);
                GoodsReceipt gr;

                if (isNew)
                {
                    gr = await _purchaseService.CreateGoodsReceiptAsync(vm, userId, plantId);
                }
                else
                {
                    // 鎖定檢查：已有倉庫作業則不可修改
                    var existing = await _context.GoodsReceipts.AsNoTracking().Include(g => g.Lines)
                        .FirstOrDefaultAsync(g => g.GoodsReceiptId == vm.GoodsReceiptId);

                    // 簽核狀態一律以 existing（資料庫）為準，不看 vm 帶回的值：_Form 的
                    // ApprovalStatus 是使用者可改的 hidden 欄位，信任它等於讓人偽造一個 POST
                    // 就能修改審核中的單，而簽核申請不會重跑。下面兩道（已上架／已退貨）本來
                    // 就讀 DB，只有這一格原本信任前端。順序沿用原本的訊息優先序：審核中先講。
                    if (existing != null
                        && GoodsReceiptStateMachine.CanEdit(existing.ApprovalStatus) != GoodsReceiptGuard.Allowed)
                    {
                        ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                        await PrepareFormDropdowns();
                        return PartialView("_Form", vm);
                    }
                    if (existing != null && existing.Lines.Any(l => l.QtyPutAway > 0))
                        throw new InvalidOperationException(PzText.Get("Pur_WarehouseWorkStarted"));
                    if (existing != null && existing.Lines.Any(l => l.ReturnedQty > 0))
                        throw new InvalidOperationException(PzText.Get("Pur_ReceiptHasReturns"));
                    // xmin 樂觀鎖的蓋章在 UpdateGoodsReceiptAsync 內部、寫入之前完成
                    // ——放在這裡（服務已經 SaveChanges 過）等於比對一個自己剛覆寫掉的基準。
                    gr = await _purchaseService.UpdateGoodsReceiptAsync(vm, userId);
                }

                gr.ApprovalStatus = targetStatus;

                // 未設簽核流程時存檔即核准並自動過帳完成。此路徑必須與手動 Confirm 一樣執行
                // 完成後置作業（委外鉤子 + 自動產生應付發票），否則核准即完成的進貨單不會出現在
                // 應付帳款。過帳＋存檔＋後置作業須為單一交易，任一失敗整批回滾。
                if (targetStatus == DocumentApprovalStatus.Approved)
                {
                    var simplified = string.Equals(
                        await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                        "true", StringComparison.OrdinalIgnoreCase);

                    await using var tx = await _context.Database.BeginTransactionAsync();
                    if (simplified && gr.Status != DocStatus.Posted)
                    {
                        var simplifiedLines = await BuildSimplifiedLinesAsync(gr);
                        await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                        {
                            PlantId = gr.PlantId ?? "",
                            DocNo = gr.GoodsReceiptId,
                            MovementDate = gr.ReceiptDate,
                            TypeCode = gr.TypeCode,
                            TypeCategory = gr.TypeCategory,
                            CreatedBy = userId,
                            Lines = simplifiedLines,
                            SetDocPosted = () => gr.Status = DocStatus.Posted,
                        });
                    }
                    else if (!simplified)
                    {
                        gr.Status = DocStatus.Confirmed;
                    }

                    await _context.SaveChangesAsync();
                    await RunGrCompletionHooksAsync(gr);
                    await tx.CommitAsync();
                }
                else
                {
                    await _context.SaveChangesAsync();
                }

                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>",
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

        // POST: /GoodsReceipt/Submit/{id} — 送審
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<GoodsReceipt>(id, MenuKey, DocType);

        // POST: /GoodsReceipt/Withdraw/{id} — 撤回
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<GoodsReceipt>(id, MenuKey);

        private async Task OnGrApprovedAsync(GoodsReceipt gr)
        {
            if (gr.Status != DocStatus.Draft) return;
            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);
            if (simplified)
            {
                // 自動過帳例外不在此吞掉：交由 SyncApprovalStatusAsync 統一以交易回滾並提示。
                var simplifiedLines = await BuildSimplifiedLinesAsync(gr);
                await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = gr.PlantId ?? "",
                    DocNo = gr.GoodsReceiptId,
                    MovementDate = gr.ReceiptDate,
                    TypeCode = gr.TypeCode,
                    TypeCategory = gr.TypeCategory,
                    CreatedBy = _userContext.UserId,
                    Lines = simplifiedLines,
                    SetDocPosted = () => gr.Status = DocStatus.Posted,
                });
            }
            else gr.Status = DocStatus.Confirmed;

            // 簽核核准同樣是一條「完成」路徑，須與手動 Confirm 一致產生應付發票，否則
            // 核准即完成的進貨單不會出現在應付帳款。本方法整段已由 SyncApprovalStatusAsync 交易包住。
            await RunGrCompletionHooksAsync(gr);
        }

        // POST: /GoodsReceipt/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            try
            {
                var doc = await _context.GoodsReceipts.AsNoTracking().Include(g => g.Lines)
                    .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
                // doc == null 交給 service 拋「找不到進貨單」，維持原本的訊息來源
                if (doc != null)
                {
                    var guard = GoodsReceiptStateMachine.CanDelete(
                        doc.Status, doc.ApprovalStatus,
                        hasPutAway: doc.Lines.Any(l => l.QtyPutAway > 0),
                        hasReturns: doc.Lines.Any(l => l.ReturnedQty > 0));
                    if (guard != GoodsReceiptGuard.Allowed)
                        return BadRequest(new { success = false, message = guard switch
                        {
                            GoodsReceiptGuard.WarehouseStarted => _localizer["HasWarehouseOpsCannotDelete"].Value,
                            GoodsReceiptGuard.HasReturns       => _localizer["HasReturnsCannotDelete"].Value,
                            GoodsReceiptGuard.PendingApproval  => _localizer["StagingErrDeletePending"].Value,
                            _                                  => _localizer["StagingErrDraftToDelete"].Value,
                        } });
                }

                await _purchaseService.DeleteGoodsReceiptAsync(id);
                return Ok(new { success = true, message = _localizer["DeleteSuccess"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // POST: /GoodsReceipt/Confirm/{id} — Draft → Confirmed（或簡化模式下 → Posted）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var gr = await _context.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
            if (gr == null) return NotFound();

            var confirmGuard = GoodsReceiptStateMachine.CanConfirm(
                gr.Status, gr.ApprovalStatus, hasLines: gr.Lines.Any());
            if (confirmGuard != GoodsReceiptGuard.Allowed)
                return BadRequest(new { success = false, message = confirmGuard switch
                {
                    GoodsReceiptGuard.NotDraft      => _localizer["StagingErrDraftToConfirm"].Value,
                    GoodsReceiptGuard.NeedsApproval => _localizer["StagingErrNeedApproval"].Value,
                    _                               => _localizer["NoLinesCannotConfirm"].Value,
                } });

            var simplified = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId),
                "true", StringComparison.OrdinalIgnoreCase);

            // 庫存過帳、委外鉤子、自動產生 AP 發票須為單一原子作業：
            // 過帳已提交但後段 OPO/AP 失敗時，整批回滾避免「庫存已過帳、發票未建」的不一致。
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                string message;
                if (simplified)
                {
                    var simplifiedLines = await BuildSimplifiedLinesAsync(gr);
                    await _postingService.PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                    {
                        PlantId = gr.PlantId ?? "",
                        DocNo = gr.GoodsReceiptId,
                        MovementDate = gr.ReceiptDate,
                        TypeCode = gr.TypeCode,
                        TypeCategory = gr.TypeCategory,
                        CreatedBy = _userContext.UserId,
                        Lines = simplifiedLines,
                        SetDocPosted = () => gr.Status = DocStatus.Posted,
                    });
                    await _context.SaveChangesAsync();
                    message = _localizer["PostedSimple"].Value;
                }
                else
                {
                    gr.Status = DocStatus.Confirmed;
                    await _context.SaveChangesAsync();
                    message = _localizer["ConfirmedAwaitingWarehouse"].Value;
                }

                // 完成後置作業：委外進貨鉤子 + 自動產生應付發票（見 RunGrCompletionHooksAsync）
                await RunGrCompletionHooksAsync(gr);
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

        /// <summary>
        /// 進貨單「完成」後置作業，三條完成路徑共用：手動 Confirm、存檔自動核准、簽核核准。
        /// 內容為委外進貨鉤子 + 自動產生應付發票（皆冪等），確保任一路徑完成後都會出現在應付帳款。
        /// 呼叫端須自行以交易包住（避免「庫存已過帳、發票未建」的不一致）。
        /// </summary>
        private async Task RunGrCompletionHooksAsync(GoodsReceipt gr)
        {
            await HandleOutsourceReceiptIfNeededAsync(gr);

            // AP 認列時點：以「貨是否已實際進正常倉」為準，不再於進貨單完成時整批認列。
            // ・簡化模式（完成即過帳 Status=Posted）：貨直接進正常倉、無待驗/上架步驟 →
            //   就地按各明細收貨量認列。
            // ・完整模式（Status=Confirmed）：貨尚待上架，不在此認列——待 put-away 進正常倉
            //   （免驗直放）或 IQC 合格放行時，由 WarehouseApiController / IqcInspectionService
            //   各按進倉量呼叫 AccrueForReceivedQtyAsync。這期間差額由 GR/IR 暫收科目承接。
            if (gr.Status == DocStatus.Posted)
            {
                // 明細改由 DB 讀取：Save 路徑的 gr 來自服務層，其 Lines 導覽未必已載入，
                // 直接迭代 gr.Lines 會漏掉（過帳已 SaveChanges，DB 必有這些明細）。
                var lines = gr.Lines.Count > 0
                    ? gr.Lines.ToList()
                    : await _context.GoodsReceiptLines
                        .Where(l => l.GoodsReceiptId == gr.GoodsReceiptId).ToListAsync();
                foreach (var line in lines)
                    if (line.ReceiptQty > 0m)
                        await _apInvoiceService.AccrueForReceivedQtyAsync(gr.GoodsReceiptId, line.LineNo, line.ReceiptQty);
            }
        }

        /// <summary>
        /// Phase G 委外進貨整合：GR 確認後更新 OPO 狀態，並為不良品建立重工工單佔位。
        /// SourceDocType == "OPO" 時觸發。
        /// </summary>
        private async Task HandleOutsourceReceiptIfNeededAsync(GoodsReceipt gr)
        {
            if (gr.SourceDocType != "OPO" || string.IsNullOrEmpty(gr.SourceDocId)) return;

            var opo = await _context.OutsourcePurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.OutsourcePurchaseOrderId == gr.SourceDocId);
            if (opo == null) return;

            // 更新 OPO：若所有行的 SplitQty 已全部進貨則 Confirmed → Closed（暫以 Confirmed 表示完成）
            var totalOrdered  = opo.Lines.Sum(l => l.SplitQty > 0 ? l.SplitQty : l.Qty);
            var totalReceived = await _context.GoodsReceiptLines
                .Where(l => _context.GoodsReceipts
                    .Where(r => r.SourceDocId == gr.SourceDocId && r.SourceDocType == "OPO")
                    .Select(r => r.GoodsReceiptId)
                    .Contains(l.GoodsReceiptId))
                .SumAsync(l => l.ReceiptQty);

            if (totalReceived >= totalOrdered && opo.Status == OutsourcePoStatus.Confirmed)
            {
                opo.Status = OutsourcePoStatus.Void; // 暫用 Void 表示完工關單；Phase H 補 Closed 狀態
            }

            await _context.SaveChangesAsync();

            // QtyRejected > 0：Phase G 規格預留重工工單建立點（Phase H 實作）
        }

        // POST: /GoodsReceipt/CancelConfirm/{id} — Confirmed → Draft（僅限無倉庫作業者）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var gr = await _context.GoodsReceipts
                .Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
            if (gr == null) return NotFound();

            var cancelGuard = GoodsReceiptStateMachine.CanCancelConfirm(
                gr.Status, hasPutAway: gr.Lines.Any(l => l.QtyPutAway > 0));
            if (cancelGuard != GoodsReceiptGuard.Allowed)
                return BadRequest(new { success = false, message = cancelGuard switch
                {
                    GoodsReceiptGuard.NotConfirmed => _localizer["ConfirmedOnlyCanWithdraw"].Value,
                    _                              => _localizer["WarehouseStartedContactStaff"].Value,
                } });

            gr.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        // POST: /GoodsReceipt/Void/{id} — Posted → Void（沖銷所有 StockMovement）
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var gr = await _context.GoodsReceipts.Include(g => g.Lines)
                .FirstOrDefaultAsync(g => g.GoodsReceiptId == id);
            if (gr == null) return NotFound();

            if (GoodsReceiptStateMachine.CanVoid(gr.Status) != GoodsReceiptGuard.Allowed)
                return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            // 同出貨：應付發票與進貨退出不會跟著作廢，先處理它們（2026-10-22 PayableLedgerModelTests 發現）
            var liveReturn = await _context.GoodsReturnLines.AsNoTracking()
                .Where(l => l.GoodsReceiptId == id && l.GoodsReturn!.Status != DocStatus.Void)
                .Select(l => l.GoodsReturnId).FirstOrDefaultAsync();
            if (liveReturn != null)
                return BadRequest(new { success = false, message = string.Format(_localizer["ReceiptVoidReturnFirst"].Value, liveReturn) });
            var liveInvoice = await _context.APInvoices.AsNoTracking()
                .Where(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == id && i.Status != Modules.FIN.Models.APInvoiceStatus.Void)
                .Select(i => i.APInvoiceId).FirstOrDefaultAsync();
            if (liveInvoice != null)
                return BadRequest(new { success = false, message = string.Format(_localizer["ReceiptVoidInvoiceFirst"].Value, liveInvoice) });

            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest
                {
                    PlantId = gr.PlantId ?? "",
                    DocNo = gr.GoodsReceiptId,
                    Reason = reason,
                    CreatedBy = _userContext.UserId,
                    SetDocVoid = () => gr.Status = DocStatus.Void,
                });
                // 作廢回沖 PO 明細累計進貨量（對齊出貨單作廢回沖 ShippedQty 的語意；
                // 否則作廢後 PO 額度被永久占用、且與 §3.1 稽核「作廢不計」矛盾）
                await _purchaseService.RestorePoReceivedQtyAsync(gr.Lines);
                await _context.SaveChangesAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        /// <summary>簡化模式：將每個 Line 映射為 SimplifiedPostLine。LocationCode 缺省時取 ProductCategory.DefaultLocationCode。</summary>
        private async Task<List<SimplifiedPostLine>> BuildSimplifiedLinesAsync(GoodsReceipt gr)
        {
            var productCodes = gr.Lines.Select(l => l.ProductCode).Distinct().ToList();
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
            foreach (var line in gr.Lines.OrderBy(l => l.LineNo))
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
                    BusinessQty = line.ReceiptQty,
                    UomCode = line.UomCode ?? "",
                    StockUomCode = line.StockUomCode ?? "",
                    UnitPrice = await GoodsReceiptCostBasis.ResolveAccrualUnitPriceAsync(_context, line),
                    ExchangeRate = line.ExchangeRate,
                    SetQtyPutAway = qty => currentLine.QtyPutAway = qty,
                });
            }
            return result;
        }

        // GET: /GoodsReceipt/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.GoodsReceipts
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.GoodsReceiptId == id && d.PlantId == _userContext.PlantId);
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

        // GET: /GoodsReceipt/Lines/{id} — 取得進貨單明細（供退貨單選入使用）
        [HttpGet]
        public async Task<IActionResult> Lines(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;
            var lines = await _context.GoodsReceiptLines
                .AsNoTracking()
                .Include(l => l.GoodsReceipt)
                .Where(l => l.GoodsReceiptId == id
                         && l.GoodsReceipt!.PlantId == plantId)
                .OrderBy(l => l.LineNo)
                .Select(l => new
                {
                    l.LineNo,
                    l.ProductCode,
                    l.ReceiptQty,
                    l.UomCode,
                    l.StockQty,
                    l.StockUomCode,
                    l.UnitPrice,
                    l.CurrencyCode,
                    l.ExchangeRate,
                    l.TaxCode,
                    l.TaxRate,
                    l.WhCode,
                    l.LocationCode,
                    l.LotNo,
                })
                .ToListAsync();

            return Json(lines);
        }

        // GET: /GoodsReceipt/AvailableLines — 取得所有可退貨進貨明細（供退貨單 Modal 多選使用）
        [HttpGet]
        public async Task<IActionResult> AvailableLines(string? searchTerm, string? supplierCode)
        {
            if (!await _permissionService.CanReadAny(MenuKey, "GoodsReturn")) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 GoodsReturn 明細加總已退貨量（排除作廢）
            var returnedRaw = await _context.GoodsReturnLines.AsNoTracking()
                .Where(rl => rl.GoodsReturn!.PlantId == plantId
                    && rl.GoodsReturn.Status != DocStatus.Void
                    && rl.GoodsReceiptId != null && rl.ReceiptLineNo.HasValue)
                .GroupBy(rl => new { rl.GoodsReceiptId, rl.ReceiptLineNo })
                .Select(g => new { g.Key.GoodsReceiptId, LineNo = g.Key.ReceiptLineNo!.Value, Returned = g.Sum(rl => rl.ReturnQty) })
                .ToListAsync();
            var returnedMap = returnedRaw.ToDictionary(x => (x.GoodsReceiptId!, x.LineNo), x => x.Returned);

            var query = _context.GoodsReceiptLines
                .AsNoTracking()
                .Include(l => l.GoodsReceipt)
                .Where(l => l.GoodsReceipt!.PlantId == plantId
                         && l.GoodsReceipt.Status == DocStatus.Posted);

            if (!string.IsNullOrEmpty(supplierCode))
                query = query.Where(l => l.GoodsReceipt!.SupplierCode == supplierCode);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(l =>
                    l.GoodsReceiptId.ToLower().Contains(lower) ||
                    l.ProductCode.ToLower().Contains(lower));
            }

            var grLines = await query
                .OrderBy(l => l.GoodsReceiptId)
                .ThenBy(l => l.LineNo)
                .Select(l => new
                {
                    l.GoodsReceiptId, l.LineNo, l.ProductCode,
                    l.ReceiptQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                    l.WhCode, l.LocationCode, l.LotNo,
                })
                .Take(200)
                .ToListAsync();

            var lines = grLines
                .Select(l =>
                {
                    var returned  = returnedMap.TryGetValue((l.GoodsReceiptId, l.LineNo), out var r) ? r : 0m;
                    var remaining = l.ReceiptQty - returned;
                    return new
                    {
                        l.GoodsReceiptId, l.LineNo, l.ProductCode,
                        l.ReceiptQty, l.UomCode, l.StockQty, l.StockUomCode,
                        l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                        l.WhCode, l.LocationCode, l.LotNo,
                        ReturnedQty = returned, RemainingQty = remaining,
                    };
                })
                .Where(l => l.RemainingQty > 0)
                .ToList();

            return Json(lines);
        }
    }
}
