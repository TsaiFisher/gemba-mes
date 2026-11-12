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
    /// <summary>借入借出單 (IB / IL / BR / LR)。</summary>
    [Authorize]
    public class LoanDocController : ModuleControllerBase
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

        private const string MenuKey = "LoanDoc";
        private const string DocType = "LoanDoc";

        private static readonly HashSet<string> ValidTypeCodes = new() { "IB", "IL", "BR", "LR" };

        public LoanDocController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IPlantParamService plantParams,
            IDocNumberService docNumberService,
            IInventoryPostingService postingService,
            IUomService uomService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _approvalService = approvalService;
            _plantParams = plantParams;
            _docNumberService = docNumberService;
            _postingService = postingService;
            _uomService = uomService;
            _currencyService = currencyService;
        
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

            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsSupplier && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            var allUnits = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var currentPlantId = _userContext.PlantId;
            ViewBag.DeptOptions = allUnits
                .Where(u => GetEffectivePlantId(u, allUnits) == currentPlantId)
                .OrderBy(u => u.Name)
                .Select(u => new { u.Id, u.Name })
                .ToList();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.TransactionTypes = await _context.InventoryTransactionTypes
                .AsNoTracking().Where(t => t.IsActive)
                .Select(t => new { t.TypeCode, t.SourceTypeCode, t.RequireDoc })
                .ToListAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        // GET: /LoanDoc
        public async Task<IActionResult> Index(
            string? searchTerm, string? typeCode, DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm;
            ViewBag.TypeCode   = typeCode;
            ViewBag.DateFrom   = dateFrom;
            ViewBag.DateTo     = dateTo;
            ViewBag.Searched   = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<LoanDoc>?)null);

            var query = _context.LoanDocs.AsNoTracking().Include(d => d.Lines).AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.LoanDocId.Contains(searchTerm) || d.EntityCode.Contains(searchTerm)
                                      || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(typeCode))
                query = query.Where(d => d.TypeCode == typeCode);
            if (dateFrom.HasValue) query = query.Where(d => d.LoanDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.LoanDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(d => d.LoanDocId),
                "id_desc"   => query.OrderByDescending(d => d.LoanDocId),
                "date_asc"  => query.OrderBy(d => d.LoanDate),
                "date_desc" => query.OrderByDescending(d => d.LoanDate),
                _           => query.OrderByDescending(d => d.LoanDocId),
            };

            var paged = await PaginatedList<LoanDoc>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            ViewBag.EntityNames = await LoadEntityNamesAsync(paged.Select(d => (d.EntityType, d.EntityCode)));
            return View(paged);
        }

        // GET: /LoanDoc/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new LoanDocFormVm { CreatedBy = _userContext.UserId ?? "", LoanDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /LoanDoc/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return NotFound();
            // 開單僅同步核准狀態顯示，不觸發自動過帳——過帳僅由使用者主動按「確認過帳」(Confirm) 觸發。
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            if (doc.Status == DocStatus.Posted || doc.Status == DocStatus.Void) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        // GET: /LoanDoc/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return NotFound();
            // 開單僅同步核准狀態顯示，不觸發自動過帳——過帳僅由使用者主動按「確認過帳」(Confirm) 觸發。
            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            // 唯讀面板上的「取消確認」依此顯示（列表對非草稿只給檢視，那是唯一入口）
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        // POST: /LoanDoc/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(LoanDocFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.LoanDocId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            if (!ValidTypeCodes.Contains(vm.TypeCode))
            {
                ModelState.AddModelError(nameof(vm.TypeCode), _localizer["OperationFailed"].Value);
                await PrepareFormDropdowns(); return PartialView("_Form", vm);
            }

            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<LoanDoc>(vm.LoanDocId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns(); return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                var isNew = string.IsNullOrEmpty(vm.LoanDocId);
                LoanDoc doc;

                if (isNew)
                {
                    doc = new LoanDoc { LoanDocId = await _docNumberService.NextAsync(DocType, plantId, vm.LoanDate), CreatedBy = userId, PlantId = plantId };
                    _context.LoanDocs.Add(doc);
                }
                else
                {
                    doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == vm.LoanDocId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_LoanDocNotFound"));
                    if (doc.Lines.Any(l => l.QtyPutAway > 0)) throw new InvalidOperationException(PzText.Get("Inv_WarehouseWorkStarted"));
                    _context.LoanDocLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 若不 Clear，下方重新 Add 新行後過帳會把殘留的舊行一併算入，
                    // 造成沿用舊行號的行被重複過帳。
                    doc.Lines.Clear();
                }

                doc.LoanDate   = vm.LoanDate;
                doc.TypeCode   = vm.TypeCode;
                doc.EntityType = vm.EntityType;
                doc.EntityCode = vm.EntityCode;
                doc.Remarks    = vm.Remarks;
                doc.UpdatedAt  = DateTime.UtcNow;
                doc.ApprovalStatus = targetStatus;

                // 借入（IB）的單價必填：借入的料領去生產時要依**借入成本**計價，
                // 而它同時是「借 存貨／貸 其他應付款—借入物料」這筆分錄的金額。
                // 單價留空時 `UnitPrice` 為 null → `BuildJournalLines` 的 totalCost <= 0 早退
                // → 一筆分錄都不產生，存貨與負債兩邊同時漏記；MA 品號的均價也不更新，
                // 於是那批料以 0 成本進倉，工單料成本與成品單價一起偏低而全站無訊號
                // （與 §3.4 ⑬ 的 0 成本庫存同一種形狀）。
                //
                // 只擋 IB：BR／IL 是出庫（成本取庫存現值），LR 借出退回帶的是原借出成本。
                if (vm.TypeCode == "IB")
                {
                    foreach (var lv in vm.Lines)
                    {
                        if (!lv.UnitPrice.HasValue || lv.UnitPrice.Value <= 0m)
                        {
                            ModelState.AddModelError(string.Empty,
                                string.Format(_localizer["LoanInboundUnitPriceRequired"].Value, lv.ProductCode));
                            await PrepareFormDropdowns(); return PartialView("_Form", vm);
                        }
                    }
                }

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
                        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, sourceCategory, lv.SourceDocId, lv.SourceLineNo.Value, vm.LoanDocId);
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
                    doc.Lines.Add(new LoanDocLine
                    {
                        LoanDocId = doc.LoanDocId, LineNo = lineNo++,
                        ProductCode = lv.ProductCode, Qty = qty, UomCode = lv.UomCode,
                        StockQty = stockQty, StockUomCode = stockUom,
                        UnitPrice = lv.UnitPrice, WhCode = lv.WhCode, LocationCode = lv.LocationCode, Remarks = lv.Remarks,
                        SourceDocId = lv.SourceDocId, SourceLineNo = lv.SourceLineNo,
                    });
                }

                if (targetStatus == DocumentApprovalStatus.Approved) await HandleApprovedAsync(doc, plantId, userId);

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
            => await SubmitForApprovalAsync<LoanDoc>(id, MenuKey, DocType);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<LoanDoc>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Draft) return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved) return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any()) return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

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
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Confirmed) return BadRequest(new { success = false, message = _localizer["ConfirmedOnlyCanWithdraw"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0)) return BadRequest(new { success = false, message = _localizer["WarehouseStartedCannotWithdraw"].Value });
            doc.Status = DocStatus.Draft;
            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = _localizer["StagingWithdrawn"].Value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.LoanDocs.FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoid"].Value });
            await using var tx1 = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest { PlantId = doc.PlantId ?? "", DocNo = doc.LoanDocId, Reason = reason, CreatedBy = _userContext.UserId, SetDocVoid = () => doc.Status = DocStatus.Void });
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
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Posted) return BadRequest(new { success = false, message = _localizer["PostedOnlyCanVoidAndReopen"].Value });
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _postingService.VoidDocAsync(new VoidDocRequest { PlantId = doc.PlantId ?? "", DocNo = doc.LoanDocId, Reason = reason ?? "作廢並重新開立", CreatedBy = _userContext.UserId, SetDocVoid = () => doc.Status = DocStatus.Void });
                var plantId = _userContext.PlantId ?? "";
                var newDoc = new LoanDoc
                {
                    LoanDocId = await _docNumberService.NextAsync(DocType, plantId, doc.LoanDate),
                    CreatedBy = _userContext.UserId ?? "", PlantId = plantId,
                    LoanDate = doc.LoanDate, TypeCode = doc.TypeCode,
                    EntityType = doc.EntityType, EntityCode = doc.EntityCode, Remarks = doc.Remarks,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
                };
                foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                    newDoc.Lines.Add(new LoanDocLine
                    {
                        LoanDocId = newDoc.LoanDocId, LineNo = l.LineNo,
                        ProductCode = l.ProductCode, Qty = l.Qty, UomCode = l.UomCode,
                        StockQty = l.StockQty, StockUomCode = l.StockUomCode, UnitPrice = l.UnitPrice,
                        WhCode = l.WhCode, LocationCode = l.LocationCode, Remarks = l.Remarks,
                    });
                _context.LoanDocs.Add(newDoc);
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
            var doc = await _context.LoanDocs.Include(d => d.Lines).FirstOrDefaultAsync(d => d.LoanDocId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Lines.Any(l => l.QtyPutAway > 0))
                return Json(new { success = false, message = _localizer["HasWarehouseOpsCannotDelete"].Value });
            _context.LoanDocs.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private LoanDocFormVm ToVm(LoanDoc doc) => new()
        {
            LoanDocId = doc.LoanDocId, LoanDate = doc.LoanDate, TypeCode = doc.TypeCode,
            EntityType = doc.EntityType, EntityCode = doc.EntityCode, Remarks = doc.Remarks,
            CreatedBy = doc.CreatedBy, Status = doc.Status, ApprovalStatus = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new LoanDocLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode, Qty = l.Qty, UomCode = l.UomCode,
                StockQty = l.StockQty, StockUomCode = l.StockUomCode, UnitPrice = l.UnitPrice,
                WhCode = l.WhCode, LocationCode = l.LocationCode, QtyPutAway = l.QtyPutAway, Remarks = l.Remarks,
                SourceDocId = l.SourceDocId, SourceLineNo = l.SourceLineNo,
            }).ToList(),
        };

        private async Task HandleApprovedAsync(LoanDoc doc, string plantId, string userId)
        {
            var simplified = await IsSimplifiedAsync();
            if (simplified && doc.Status != DocStatus.Posted) await _postingService.PostDocSimplifiedAsync(BuildSimplifiedRequest(doc, userId));
            else if (!simplified) doc.Status = DocStatus.Confirmed;
        }

        private async Task<bool> IsSimplifiedAsync() => string.Equals(
            await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, _userContext.PlantId), "true", StringComparison.OrdinalIgnoreCase);

        private PostDocSimplifiedRequest BuildSimplifiedRequest(LoanDoc doc, string? userId)
        {
            var lines = doc.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var line = l;
                return new SimplifiedPostLine
                {
                    LineNo = l.LineNo, ProductCode = l.ProductCode,
                    LocationCode = l.LocationCode ?? "", WhCode = l.WhCode ?? "", LotNo = "",
                    Qty = l.StockQty, UomCode = l.UomCode ?? "", StockUomCode = l.StockUomCode ?? "",
                    UnitPrice = l.UnitPrice, SetQtyPutAway = qty => line.QtyPutAway = qty,
                    SourceDocId = l.SourceDocId, SourceLineNo = l.SourceLineNo,
                };
            }).ToList();
            return new PostDocSimplifiedRequest
            {
                PlantId = doc.PlantId ?? "", DocNo = doc.LoanDocId, MovementDate = doc.LoanDate,
                TypeCode = doc.TypeCode, TypeCategory = doc.TypeCategory, CreatedBy = userId,
                Lines = lines, SetDocPosted = () => doc.Status = DocStatus.Posted,
            };
        }

        /// <summary>對象代號 → 顯示名稱：部門存的是 OrganizationUnit.Id（顯示名稱），客戶／供應商存 CompanyCode（顯示「代號 — 名稱」）。</summary>
        private async Task<Dictionary<string, string>> LoadEntityNamesAsync(IEnumerable<(string? Type, string? Code)> entities)
        {
            var list = entities.Where(e => !string.IsNullOrEmpty(e.Code)).ToList();
            var deptIds = list.Where(e => e.Type == "Department").Select(e => e.Code!).Distinct().ToList();
            var companyCodes = list.Where(e => e.Type != "Department").Select(e => e.Code!).Distinct().ToList();
            var names = new Dictionary<string, string>();
            if (deptIds.Count > 0)
                foreach (var u in await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking()
                             .Where(u => deptIds.Contains(u.Id)).Select(u => new { u.Id, u.Name }).ToListAsync())
                    names[u.Id] = u.Name;
            if (companyCodes.Count > 0)
                foreach (var c in await _context.Companies.IgnoreQueryFilters().AsNoTracking()
                             .Where(c => companyCodes.Contains(c.CompanyCode)).Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync())
                    names[c.CompanyCode] = $"{c.CompanyCode} — {c.CompanyName}";
            return names;
        }

        private string? GetEffectivePlantId(OrganizationUnit unit, List<OrganizationUnit> allUnits)
        {
            if (!string.IsNullOrEmpty(unit.PlantId)) return unit.PlantId;
            if (string.IsNullOrEmpty(unit.ParentId)) return null;
            var parent = allUnits.FirstOrDefault(u => u.Id == unit.ParentId);
            return parent == null ? null : GetEffectivePlantId(parent, allUnits);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.LoanDocs
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LoanDocId == id && d.PlantId == _userContext.PlantId);
            if (doc == null) return NotFound();
            var entityNames = await LoadEntityNamesAsync(new[] { (doc.EntityType, doc.EntityCode) });
            ViewBag.EntityName = doc.EntityCode != null && entityNames.TryGetValue(doc.EntityCode, out var en) ? en : doc.EntityCode;
            await PreparePrintViewBagAsync(
                Url.Action("Edit", null, new { id }, Request.Scheme)!,
                new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            return View(doc);
        }
    }
}
