using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.INV.Controllers
{
    /// <summary>採購分攤費用（Landed Cost）分攤單 (LCA)。規格 §3.4。</summary>
    [Authorize]
    public class LandedCostAllocationController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly ILandedCostService _landedCostService;
        private readonly ICurrencyService _currencyService;
        private readonly IExportService _exportService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "LandedCostAllocation";
        private const string DocType = "LandedCostAllocation";

        public LandedCostAllocationController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IDocNumberService docNumberService,
            ILandedCostService landedCostService,
            ICurrencyService currencyService,
            IExportService exportService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _approvalService = approvalService;
            _docNumberService = docNumberService;
            _landedCostService = landedCostService;
            _currencyService = currencyService;
            _exportService = exportService;
        
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
            ViewBag.CostTypeOptions = await _context.CodeEntries
                .AsNoTracking().Where(e => e.Category == "landed_cost_type" && e.IsActive)
                .OrderBy(e => e.SortOrder).ToDisplayOptionsAsync();

            ViewBag.CurrencyOptions = await _currencyService.GetActiveCurrenciesAsync();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.BaseCurrency = await _currencyService.GetBaseCurrencyAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        // GET: /LandedCostAllocation
        public async Task<IActionResult> Index(
            string? searchTerm, DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm;
            ViewBag.DateFrom = dateFrom;
            ViewBag.DateTo = dateTo;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.IdSort   = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();

            await PrepareIndexPermissionsAsync(MenuKey);

            if (!searched) return View((PaginatedList<LandedCostAllocation>?)null);

            var query = _context.LandedCostAllocations.AsNoTracking().Include(d => d.Lines).AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.LandedCostAllocationId.Contains(searchTerm)
                                      || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (dateFrom.HasValue) query = query.Where(d => d.DocDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.DocDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(d => d.LandedCostAllocationId),
                "id_desc"   => query.OrderByDescending(d => d.LandedCostAllocationId),
                "date_asc"  => query.OrderBy(d => d.DocDate),
                "date_desc" => query.OrderByDescending(d => d.DocDate),
                _           => query.OrderByDescending(d => d.LandedCostAllocationId),
            };

            var paged = await PaginatedList<LandedCostAllocation>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        // GET: /LandedCostAllocation/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new LandedCostAllocationFormVm
            {
                CreatedBy = _userContext.UserId ?? "",
                DocDate = _dateTimeService.GetLocalDate(),
                CurrencyCode = await _currencyService.GetBaseCurrencyAsync(),
            };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /LandedCostAllocation/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.LandedCostAllocations.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == id);
            if (doc == null) return NotFound();

            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            if (doc.Status != DocStatus.Draft) ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        // GET: /LandedCostAllocation/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var doc = await _context.LandedCostAllocations.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == id);
            if (doc == null) return NotFound();

            await SyncApprovalStatusAsync(doc);
            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;
            ViewBag.CanVoid = doc.Status == DocStatus.Confirmed && await _permissionService.CanUpdate(MenuKey);
            return IsAjax ? PartialView("_Form", ToVm(doc)) : RedirectToAction(nameof(Index));
        }

        // POST: /LandedCostAllocation/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(LandedCostAllocationFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.LandedCostAllocationId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            if (vm.Lines.Count == 0)
            {
                ModelState.AddModelError(string.Empty, _localizer["SelectReceiptLineFirst"].Value);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }

            try
            {
                var userId  = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";
                var currency = vm.CurrencyCode ?? await _currencyService.GetBaseCurrencyAsync() ?? "";

                // 簽核狀態一律讀資料庫：vm 的 ApprovalStatus 是使用者可改的 hidden 欄位，
                // 採信它等於讓人偽造一個 POST 就能改掉審核中的單（見 LoadApprovalStatusAsync）
                if (await _context.LoadApprovalStatusAsync<LandedCostAllocation>(vm.LandedCostAllocationId) == DocumentApprovalStatus.PendingApproval)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns();
                    return PartialView("_Form", vm);
                }

                var config        = await _approvalService.GetApprovalConfig(DocType, plantId);
                var needsApproval = config?.ApprovalTemplateId != null;
                var targetStatus  = needsApproval ? DocumentApprovalStatus.Draft : DocumentApprovalStatus.Approved;

                var isNew = string.IsNullOrEmpty(vm.LandedCostAllocationId);
                LandedCostAllocation doc;

                if (isNew)
                {
                    doc = new LandedCostAllocation
                    {
                        LandedCostAllocationId = await _docNumberService.NextAsync(DocType, plantId, vm.DocDate),
                        CreatedBy = userId,
                        PlantId   = plantId,
                    };
                    _context.LandedCostAllocations.Add(doc);
                }
                else
                {
                    doc = await _context.LandedCostAllocations.Include(d => d.Lines)
                              .FirstOrDefaultAsync(d => d.LandedCostAllocationId == vm.LandedCostAllocationId)
                          ?? throw new InvalidOperationException(PzText.Get("Inv_LandedCostNotFound"));
                    if (doc.Status != DocStatus.Draft)
                        throw new InvalidOperationException(PzText.Get("Inv_OnlyDraftEditable"));
                    _context.Entry(doc).Property(x => x.xmin).OriginalValue = vm.RowVersion;
                    _context.LandedCostAllocationLines.RemoveRange(doc.Lines);
                    // RemoveRange 僅標記舊行為 Deleted，不會從 doc.Lines 記憶體集合移除；
                    // 為與其餘單據一致並避免日後接上即時過帳時踩雷，明確清空。
                    doc.Lines.Clear();
                }

                doc.DocDate         = vm.DocDate;
                doc.Basis           = vm.Basis;
                doc.CostType        = vm.CostType;
                doc.CurrencyCode    = currency;
                doc.TotalLandedCost = await _currencyService.RoundAsync(vm.TotalLandedCost, currency);
                doc.Remarks         = vm.Remarks;
                doc.UpdatedAt       = DateTime.UtcNow;
                doc.ApprovalStatus  = targetStatus;

                var lineNo = 1;
                foreach (var lv in vm.Lines.OrderBy(l => l.LineNo))
                {
                    doc.Lines.Add(new LandedCostAllocationLine
                    {
                        LandedCostAllocationId = doc.LandedCostAllocationId,
                        LineNo           = lineNo++,
                        GoodsReceiptId   = lv.GoodsReceiptId,
                        GoodsReceiptLineNo = lv.GoodsReceiptLineNo,
                        ProductCode      = lv.ProductCode,
                        LocationCode     = lv.LocationCode,
                        LotNo            = lv.LotNo,
                        ReceiptQty       = lv.ReceiptQty,
                        PurchaseAmount   = await _currencyService.RoundAsync(lv.PurchaseAmount, currency),
                        Weight           = lv.Weight,
                        OriginalUnitCost = lv.OriginalUnitCost,
                        Remarks          = lv.Remarks,
                    });
                }

                await _context.SaveChangesAsync();
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareFormDropdowns();
                return PartialView("_Form", vm);
            }
        }

        // POST: /LandedCostAllocation/Submit/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<LandedCostAllocation>(id, MenuKey, DocType);

        // POST: /LandedCostAllocation/Withdraw/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<LandedCostAllocation>(id, MenuKey);

        // POST: /LandedCostAllocation/Confirm/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirm(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.LandedCostAllocations.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == id);
            if (doc == null) return NotFound();
            if (doc.Status != DocStatus.Draft)
                return BadRequest(new { success = false, message = _localizer["StagingErrDraftToConfirm"].Value });
            if (doc.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["StagingErrNeedApproval"].Value });
            if (!doc.Lines.Any())
                return BadRequest(new { success = false, message = _localizer["NoLinesCannotConfirm"].Value });

            try
            {
                await _landedCostService.ConfirmAsync(id);
                return Ok(new { success = true, message = _localizer["AllocationConfirmed"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (ArgumentException ex)          { return BadRequest(new { success = false, message = ex.Message }); }
        }

        // POST: /LandedCostAllocation/Void/{id}
        /// <summary>作廢已確認的分攤單：反向傳票＋扣回均價／成本層（確認後有出庫則擋下，見 ILandedCostService.VoidAsync）。</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Void(string id, string? reason)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var exists = await _context.LandedCostAllocations.AnyAsync(d => d.LandedCostAllocationId == id);
            if (!exists) return NotFound();

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                await _landedCostService.VoidAsync(id, _dateTimeService.GetLocalDate(), reason);
                await tx.CommitAsync();
                return Ok(new { success = true, message = _localizer["VoidedAndReversed"].Value });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { success = false, message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { success = false, message = _localizer["ConcurrencyConflict"].Value }); }
        }

        // POST: /LandedCostAllocation/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            var doc = await _context.LandedCostAllocations.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == id);
            if (doc == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            if (doc.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (doc.Status != DocStatus.Draft)
                return Json(new { success = false, message = _localizer["ConfirmedAllocationCannotDelete"].Value });

            _context.LandedCostAllocations.Remove(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        // GET: /LandedCostAllocation/GoodsReceiptLines?term=...
        // 進貨明細選入器：回傳本廠已確認/過帳之進貨明細，含分攤所需快照欄位。
        [HttpGet]
        public async Task<IActionResult> GoodsReceiptLines(string? term)
        {
            if (!await _permissionService.CanCreate(MenuKey) && !await _permissionService.CanUpdate(MenuKey))
                return Forbid();
            var plantId = _userContext.PlantId ?? "";

            var grQuery = _context.GoodsReceipts.AsNoTracking()
                .Where(g => g.Status == DocStatus.Confirmed || g.Status == DocStatus.Posted);
            if (!string.IsNullOrWhiteSpace(term))
                grQuery = grQuery.Where(g => g.GoodsReceiptId.Contains(term));

            var grs = await grQuery
                .Include(g => g.Lines)
                .OrderByDescending(g => g.GoodsReceiptId)
                .Take(50)
                .ToListAsync();

            var productCodes = grs.SelectMany(g => g.Lines).Select(l => l.ProductCode).Distinct().ToList();

            var products = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.ProductName, p.NetWeight })
                .ToDictionaryAsync(p => p.ProductCode, p => p);

            var invs = await _context.Inventories.AsNoTracking()
                .Where(i => i.PlantId == plantId && productCodes.Contains(i.ProductCode))
                .ToListAsync();

            var rows = new List<object>();
            foreach (var g in grs)
            {
                foreach (var l in g.Lines.OrderBy(x => x.LineNo))
                {
                    if (!string.IsNullOrWhiteSpace(term)
                        && !g.GoodsReceiptId.Contains(term, StringComparison.OrdinalIgnoreCase)
                        && !l.ProductCode.Contains(term, StringComparison.OrdinalIgnoreCase))
                        continue;

                    products.TryGetValue(l.ProductCode, out var prod);
                    var qty = l.StockQty > 0 ? l.StockQty : l.ReceiptQty;
                    var inv = invs.FirstOrDefault(i => i.ProductCode == l.ProductCode
                        && i.LocationCode == (l.LocationCode ?? "")
                        && i.LotNo == (l.LotNo ?? ""));
                    var originalUnitCost = inv?.UnitCost
                        ?? (qty > 0 ? Math.Round(l.LineAmount / qty, 6, MidpointRounding.AwayFromZero) : l.UnitPrice);
                    var weight = qty * (prod?.NetWeight ?? 0m);

                    rows.Add(new
                    {
                        goodsReceiptId = g.GoodsReceiptId,
                        goodsReceiptLineNo = l.LineNo,
                        productCode = l.ProductCode,
                        productName = prod?.ProductName ?? "",
                        locationCode = l.LocationCode ?? "",
                        lotNo = l.LotNo ?? "",
                        receiptQty = qty,
                        purchaseAmount = l.LineAmount,
                        weight,
                        originalUnitCost = originalUnitCost,
                        currencyCode = l.CurrencyCode ?? "",
                    });
                }
            }

            return Json(rows);
        }

        // GET: /LandedCostAllocation/Export
        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, DateOnly? dateFrom, DateOnly? dateTo,
            string? sortOrder, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var query = _context.LandedCostAllocations.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(d => d.LandedCostAllocationId.Contains(searchTerm)
                                      || (d.Remarks != null && d.Remarks.Contains(searchTerm)));
            if (dateFrom.HasValue) query = query.Where(d => d.DocDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(d => d.DocDate <= dateTo.Value);

            var data = await query.OrderByDescending(d => d.LandedCostAllocationId).ToListAsync();
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var localNow = _dateTimeService.Now;

            var req = new ExportRequest
            {
                Title = "LandedCostAllocation",
                Headers = isZh
                    ? new[] { "分攤單號", "單據日期", "分攤基準", "費用類別", "待分攤費用總額", "幣別", "狀態", "備註" }
                    : new[] { "Doc No.", "Doc Date", "Basis", "Cost Type", "Total Landed Cost", "Currency", "Status", "Remarks" },
                Rows = data.Select(d => new object?[]
                {
                    d.LandedCostAllocationId,
                    d.DocDate.ToString("yyyy-MM-dd"),
                    d.Basis.ToString(),
                    d.CostType,
                    d.TotalLandedCost,
                    d.CurrencyCode,
                    d.Status.ToString(),
                    d.Remarks,
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                              Uri.EscapeDataString($"LandedCostAllocation_{localNow:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                              Uri.EscapeDataString($"LandedCostAllocation_{localNow:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                              "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                              Uri.EscapeDataString($"LandedCostAllocation_{_userContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // ── private helpers ──────────────────────────────────────────────────

        private LandedCostAllocationFormVm ToVm(LandedCostAllocation doc) => new()
        {
            LandedCostAllocationId = doc.LandedCostAllocationId,
            DocDate         = doc.DocDate,
            Basis           = doc.Basis,
            CostType        = doc.CostType,
            TotalLandedCost = doc.TotalLandedCost,
            CurrencyCode    = doc.CurrencyCode,
            Remarks         = doc.Remarks,
            CreatedBy       = doc.CreatedBy,
            Status          = doc.Status,
            ApprovalStatus  = doc.ApprovalStatus,
            ApprovalRequestId = doc.ApprovalRequestId,
            RowVersion      = doc.xmin,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new LandedCostAllocationLineVm
            {
                LineNo = l.LineNo,
                GoodsReceiptId = l.GoodsReceiptId,
                GoodsReceiptLineNo = l.GoodsReceiptLineNo,
                ProductCode = l.ProductCode,
                LocationCode = l.LocationCode,
                LotNo = l.LotNo,
                ReceiptQty = l.ReceiptQty,
                PurchaseAmount = l.PurchaseAmount,
                Weight = l.Weight,
                OriginalUnitCost = l.OriginalUnitCost,
                AllocatedCost = l.AllocatedCost,
                CapitalizedCost = l.CapitalizedCost,
                ExpensedCost = l.ExpensedCost,
                NewUnitCost = l.NewUnitCost,
                Remarks = l.Remarks,
            }).ToList(),
        };

        // GET: /LandedCostAllocation/Print/{id}（2026-10-27）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();
            var doc = await Db.LandedCostAllocations.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!, new[] { doc.CreatedBy });
            await PreparePrintSignAsync(id, doc.CreatedBy);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var t = ScreenText("INV", "LandedCostAllocation");
            var p = new PrintDocument
            {
                Title = L["Print_Title_LandedCostAllocation"], DocNo = doc.LandedCostAllocationId,
                DateLabel = L["Print_DocDate"], DateValue = doc.DocDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_AllocationBasis"], t["Basis_" + doc.Basis])
                .Field(L["Print_CostType"], doc.CostType)
                .Field(L["Print_Currency"], n.CurrencyWith(doc.CurrencyCode))
                .Field(L["Print_TotalLandedCost"], f.Money(doc.TotalLandedCost, doc.CurrencyCode))
                .Field(L["Print_Status"], DocEnumDisplay.Text(L, doc.Status))
                .Field(L["Print_ApprovalStatus"], DocStatusLabels.Label(doc.ApprovalStatus))
                .Field(L["Print_CreatedBy"], PrintUser(doc.CreatedBy))
                .Field(L["Print_Remarks"], doc.Remarks);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_GoodsReceipt"]).Col(L["Print_ProductCode"]).Col(L["Print_ProductName"])
                .Col(L["Print_Location"]).Col(L["Print_LotNo"]).Col(L["Print_Qty"], true)
                .Col(L["Print_PurchaseAmount"], true).Col(L["Print_AllocatedCost"], true)
                .Col(L["Print_OriginalUnitCost"], true).Col(L["Print_NewUnitCost"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
                tbl.Row(l.LineNo.ToString(), $"{l.GoodsReceiptId}-{l.GoodsReceiptLineNo}", l.ProductCode, n.Product(l.ProductCode),
                    n.LocationWith(l.LocationCode), l.LotNo, f.Qty(l.ReceiptQty, null),
                    f.Local(l.PurchaseAmount), f.Local(l.AllocatedCost),
                    f.Local(l.OriginalUnitCost), f.Local(l.NewUnitCost));
            p.Tables.Add(tbl);
            p.Total($"{L["Print_AllocatedCost"]}：{f.Local(doc.Lines.Sum(x => x.AllocatedCost))}");
            return PrintDocumentView(p);
        }
    }
}
