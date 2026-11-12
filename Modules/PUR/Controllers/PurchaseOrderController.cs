using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    [Authorize]
    public class PurchaseOrderController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPurchaseService _purchaseService;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "PurchaseOrder";
        private const string DocType = "PurchaseOrder";

        public PurchaseOrderController(
            ApplicationDbContext context,
            IPurchaseService purchaseService,
            IUserContext userContext,
            IPermissionService permissionService,
            IDateTimeService dateTimeService,
            IApprovalService approvalService,
            IUomService uomService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _purchaseService = purchaseService;
            _userContext = userContext;
            _permissionService = permissionService;
            _dateTimeService = dateTimeService;
            _approvalService = approvalService;
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
            ViewBag.SupplierOptions = await _context.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new
                {
                    c.CompanyCode, c.CompanyName,
                    c.QualificationStatus, c.QualificationExpiry,
                    // 供應商基本資料的預設幣別/稅別：選定供應商後由前端帶入明細，
                    // 避免明細留空而以本幣、零稅率結算
                    c.CurrencyCode, c.TaxCode, c.TaxRate
                })
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

            // 貿易條件（Incoterms）。與銷貨訂單共用同一個代碼類別——同一組 Incoterms
            // 兩邊通用，各開一份只會讓兩邊慢慢長出不同的代碼。
            ViewBag.TradeTermOptions = await _context.CodeEntries
                .AsNoTracking().Where(e => e.Category == "trade_term" && e.IsActive)
                .OrderBy(e => e.SortOrder).ToDisplayOptionsAsync();

            ViewBag.UomDecimals      = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            // 明細本幣金額（AmountLocal）以本位幣精度顯示與加總
            ViewBag.BaseCurrency     = await _currencyService.GetBaseCurrencyAsync() ?? "";
            ViewBag.BaseCurrencyDp   = await ResolveBaseCurrencyDpAsync();
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
        }

        // GET: /PurchaseOrder
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

            if (!searched) return View((PaginatedList<PurchaseOrder>?)null);

            var query = _context.PurchaseOrders
                .AsNoTracking()
                .Include(po => po.Lines)
                .Where(po => po.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(po => po.PurchaseOrderId.Contains(searchTerm) || po.Remarks!.Contains(searchTerm));

            if (!string.IsNullOrWhiteSpace(supplierCode))
                query = query.Where(po => po.SupplierCode == supplierCode);

            if (dateFrom.HasValue)
                query = query.Where(po => po.OrderDate >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(po => po.OrderDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(po => po.PurchaseOrderId),
                "id_desc"   => query.OrderByDescending(po => po.PurchaseOrderId),
                "date_asc"  => query.OrderBy(po => po.OrderDate),
                "date_desc" => query.OrderByDescending(po => po.OrderDate),
                _            => query.OrderByDescending(po => po.PurchaseOrderId),
            };

            var paged = await PaginatedList<PurchaseOrder>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20);
            return View(paged);
        }

        // GET: /PurchaseOrder/Create
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new PurchaseOrderFormVm { CreatedBy = _userContext.UserId ?? "", OrderDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /PurchaseOrder/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

            var po = await _context.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseOrderId == id);
            if (po == null) return NotFound();

            await SyncApprovalStatusAsync(po);

            await PrepareFormDropdowns();

            var vm = new PurchaseOrderFormVm
            {
                PurchaseOrderId  = po.PurchaseOrderId,
                ApprovalStatus   = po.ApprovalStatus,
                RowVersion       = po.xmin,
                OrderDate        = po.OrderDate,
                CreatedBy        = po.CreatedBy,
                SupplierCode     = po.SupplierCode,
                TradeTermCode    = po.TradeTermCode,
                DeliveryAddress  = po.DeliveryAddress,
                Remarks          = po.Remarks,
                Lines = po.Lines.OrderBy(l => l.LineNo).Select(l => new PurchaseOrderLineVm
                {
                    LineNo            = l.LineNo,
                    PurchaseRequestId = l.PurchaseRequestId,
                    PrLineNo          = l.PrLineNo,
                    ProductCode       = l.ProductCode,
                    DeliveryDate      = l.DeliveryDate,
                    OrderQty          = l.OrderQty,
                    UomCode           = l.UomCode,
                    StockQty          = l.StockQty,
                    StockUomCode      = l.StockUomCode,
                    UnitPrice         = l.UnitPrice,
                    CurrencyCode      = l.CurrencyCode,
                    ExchangeRate      = l.ExchangeRate,
                    TaxCode           = l.TaxCode,
                    TaxRate           = l.TaxRate,
                    LineAmount        = l.LineAmount,
                    TaxAmount         = l.TaxAmount,
                    TotalAmount       = l.TotalAmount,
                    AmountLocal       = l.AmountLocal,
                    Remarks           = l.Remarks,
                    IsClosed          = l.IsClosed,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // GET: /PurchaseOrder/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var po = await _context.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PurchaseOrderId == id);
            if (po == null) return NotFound();

            await PrepareFormDropdowns();
            ViewBag.IsReadOnly = true;

            var vm = new PurchaseOrderFormVm
            {
                PurchaseOrderId  = po.PurchaseOrderId,
                ApprovalStatus   = po.ApprovalStatus,
                RowVersion       = po.xmin,
                OrderDate        = po.OrderDate,
                CreatedBy        = po.CreatedBy,
                SupplierCode     = po.SupplierCode,
                TradeTermCode    = po.TradeTermCode,
                DeliveryAddress  = po.DeliveryAddress,
                Remarks          = po.Remarks,
                Lines = po.Lines.OrderBy(l => l.LineNo).Select(l => new PurchaseOrderLineVm
                {
                    LineNo            = l.LineNo,
                    PurchaseRequestId = l.PurchaseRequestId,
                    PrLineNo          = l.PrLineNo,
                    ProductCode       = l.ProductCode,
                    DeliveryDate      = l.DeliveryDate,
                    OrderQty          = l.OrderQty,
                    UomCode           = l.UomCode,
                    StockQty          = l.StockQty,
                    StockUomCode      = l.StockUomCode,
                    UnitPrice         = l.UnitPrice,
                    CurrencyCode      = l.CurrencyCode,
                    ExchangeRate      = l.ExchangeRate,
                    TaxCode           = l.TaxCode,
                    TaxRate           = l.TaxRate,
                    LineAmount        = l.LineAmount,
                    TaxAmount         = l.TaxAmount,
                    TotalAmount       = l.TotalAmount,
                    AmountLocal       = l.AmountLocal,
                    Remarks           = l.Remarks,
                    IsClosed          = l.IsClosed,
                }).ToList()
            };

            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        // POST: /PurchaseOrder/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(PurchaseOrderFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.PurchaseOrderId)
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

                // 簽核狀態一律以資料庫為準，不看 vm 帶回的 hidden 欄位（見 LoadApprovalStatusAsync）。
                // 單據不存在時回 null、不在此擋，交給下方 UpdatePurchaseOrderAsync 報「找不到」。
                var dbApproval = await _context.LoadApprovalStatusAsync<PurchaseOrder>(vm.PurchaseOrderId);
                if (dbApproval.HasValue
                    && PurchaseOrderStateMachine.CanEdit(dbApproval.Value) != PurchaseOrderGuard.Allowed)
                {
                    ModelState.AddModelError(string.Empty, _localizer["PendingApprovalCannotEdit"].Value);
                    await PrepareFormDropdowns();
                    return PartialView("_Form", vm);
                }

                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                PurchaseOrder po;
                if (string.IsNullOrEmpty(vm.PurchaseOrderId))
                {
                    po = await _purchaseService.CreatePurchaseOrderAsync(vm, userId, plantId);
                    po.ApprovalStatus = targetStatus;
                }
                else
                {
                    po = await _purchaseService.UpdatePurchaseOrderAsync(vm, userId);
                    po.ApprovalStatus = targetStatus;
                    // xmin 樂觀鎖的蓋章在 UpdatePurchaseOrderAsync 內部、寫入之前完成
                }
                await _context.SaveChangesAsync();

                if (Request.HasFormContentType && Request.Form["submit"] == "save_and_submit")
                {
                    var config = await _approvalService.GetApprovalConfig(DocType, plantId);
                    if (config?.ApprovalTemplateId == null)
                    {
                        po.ApprovalStatus = DocumentApprovalStatus.Approved;
                    }
                    else
                    {
                        var approvalReq = await _approvalService.SubmitForApproval(po.PurchaseOrderId, DocType, config.ApprovalTemplateId, userId);
                        po.ApprovalRequestId = approvalReq.Id;
                        po.ApprovalStatus    = DocumentApprovalStatus.PendingApproval;
                    }
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

        // POST: /PurchaseOrder/Submit/{id} — 送審
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
            => await SubmitForApprovalAsync<PurchaseOrder>(id, MenuKey, DocType);

        // POST: /PurchaseOrder/Withdraw/{id} — 撤回
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<PurchaseOrder>(id, MenuKey);

        // POST: /PurchaseOrder/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

            try
            {
                // doc == null 交給 service 拋「找不到採購單」，維持原本的訊息來源
                var doc = await _context.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
                    .FirstOrDefaultAsync(p => p.PurchaseOrderId == id);
                if (doc != null)
                {
                    var guard = PurchaseOrderStateMachine.CanDelete(
                        doc.ApprovalStatus, hasReceipts: doc.Lines.Any(l => l.ReceivedQty > 0));
                    if (guard != PurchaseOrderGuard.Allowed)
                        return BadRequest(new { success = false, message = guard switch
                        {
                            PurchaseOrderGuard.HasReceipts => _localizer["HasReceiptsCannotDelete"].Value,
                            _                              => _localizer["StagingErrDeletePending"].Value,
                        } });
                }

                await _purchaseService.DeletePurchaseOrderAsync(id);
                return Ok(new { success = true, message = _localizer["DeleteSuccess"].Value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        // GET: /PurchaseOrder/OpenLines/{id} — 取得未完全收貨的採購明細（供進貨單選入使用）
        [HttpGet]
        public async Task<IActionResult> OpenLines(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 GR 明細加總此 PO 的已收貨量（排除作廢）
            var receivedRaw = await _context.GoodsReceiptLines.AsNoTracking()
                .Where(gl => gl.GoodsReceipt!.PlantId == plantId
                    && gl.GoodsReceipt.Status != DocStatus.Void
                    && gl.PurchaseOrderId == id && gl.PoLineNo.HasValue)
                .GroupBy(gl => gl.PoLineNo)
                .Select(g => new { PoLineNo = g.Key!.Value, Received = g.Sum(gl => gl.ReceiptQty) })
                .ToListAsync();
            var receivedMap = receivedRaw.ToDictionary(x => x.PoLineNo, x => x.Received);

            var poLines = await _context.PurchaseOrderLines.AsNoTracking().Include(l => l.PurchaseOrder)
                .Where(l => l.PurchaseOrderId == id
                         && l.PurchaseOrder!.PlantId == plantId
                         && l.PurchaseOrder.ApprovalStatus == DocumentApprovalStatus.Approved
                         && !l.IsClosed)
                .OrderBy(l => l.LineNo)
                .Select(l => new
                {
                    l.LineNo, l.ProductCode, l.DeliveryDate,
                    l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                }).ToListAsync();

            var lines = poLines
                .Select(l =>
                {
                    var received  = receivedMap.TryGetValue(l.LineNo, out var r) ? r : 0m;
                    var remaining = l.OrderQty - received;
                    return new
                    {
                        l.LineNo, l.ProductCode, l.DeliveryDate,
                        l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                        l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                        ReceivedQty = received, RemainingQty = remaining,
                    };
                })
                .Where(l => l.RemainingQty > 0)
                .ToList();

            return Json(lines);
        }

        // GET: /PurchaseOrder/Print/{id}
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.PurchaseOrders
                .AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.PurchaseOrderId == id && d.PlantId == _userContext.PlantId);
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

        // GET: /PurchaseOrder/AvailableLines — 取得所有可收貨採購明細（供 GR Modal 多選使用）
        [HttpGet]
        public async Task<IActionResult> AvailableLines(string? searchTerm, string? supplierCode)
        {
            if (!await _permissionService.CanReadAny(MenuKey, "GoodsReceipt")) return Forbid();
            var plantId = _userContext.PlantId;

            // 動態計算：從 GR 明細加總已收貨量（排除作廢）
            var receivedRaw = await _context.GoodsReceiptLines.AsNoTracking()
                .Where(gl => gl.GoodsReceipt!.PlantId == plantId
                    && gl.GoodsReceipt.Status != DocStatus.Void
                    && gl.PurchaseOrderId != null && gl.PoLineNo.HasValue)
                .GroupBy(gl => new { gl.PurchaseOrderId, gl.PoLineNo })
                .Select(g => new { g.Key.PurchaseOrderId, PoLineNo = g.Key.PoLineNo!.Value, Received = g.Sum(gl => gl.ReceiptQty) })
                .ToListAsync();
            var receivedMap = receivedRaw.ToDictionary(x => (x.PurchaseOrderId!, x.PoLineNo), x => x.Received);

            var query = _context.PurchaseOrderLines
                .AsNoTracking()
                .Include(l => l.PurchaseOrder)
                .Where(l => l.PurchaseOrder!.PlantId == plantId
                         && l.PurchaseOrder.ApprovalStatus == DocumentApprovalStatus.Approved
                         && !l.IsClosed);

            if (!string.IsNullOrEmpty(supplierCode))
                query = query.Where(l => l.PurchaseOrder!.SupplierCode == supplierCode);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(l =>
                    l.PurchaseOrderId.ToLower().Contains(lower) ||
                    l.ProductCode.ToLower().Contains(lower));
            }

            var poLines = await query
                .OrderBy(l => l.PurchaseOrderId)
                .ThenBy(l => l.LineNo)
                .Select(l => new
                {
                    l.PurchaseOrderId, l.LineNo, l.ProductCode, l.DeliveryDate,
                    l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                })
                .Take(200)
                .ToListAsync();

            var lines = poLines
                .Select(l =>
                {
                    var received  = receivedMap.TryGetValue((l.PurchaseOrderId, l.LineNo), out var r) ? r : 0m;
                    var remaining = l.OrderQty - received;
                    return new
                    {
                        l.PurchaseOrderId, l.LineNo, l.ProductCode, l.DeliveryDate,
                        l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                        l.UnitPrice, l.CurrencyCode, l.ExchangeRate, l.TaxCode, l.TaxRate,
                        ReceivedQty = received, RemainingQty = remaining,
                    };
                })
                .Where(l => l.RemainingQty > 0)
                .ToList();

            return Json(lines);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseDoc(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var doc = await _context.PurchaseOrders.Include(d => d.Lines).FirstOrDefaultAsync(d => d.PurchaseOrderId == id);
            if (doc == null) return NotFound();
            if (PurchaseOrderStateMachine.CanClose(doc.ApprovalStatus) != PurchaseOrderGuard.Allowed)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            foreach (var l in doc.Lines.Where(l => !l.IsClosed))
                l.IsClosed = true;
            await _context.SaveChangesAsync();
            // §21.6：採購單結案使在途供給減少，標記受影響品號的 Executed MrpRun 為 dirty（建議重跑）。
            await MarkRelatedMrpRunsDirtyByProductAsync(doc.PlantId, doc.Lines.Select(l => l.ProductCode));
            return Ok(new { success = true });
        }

        /// <summary>§21.6：依品號標記同廠已展開（Executed）的 MrpRun 為 dirty（PO 結案/取消影響 Fstock 供給面）。</summary>
        private async Task MarkRelatedMrpRunsDirtyByProductAsync(string? plantId, IEnumerable<string> productCodes)
        {
            var products = productCodes.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            if (products.Count == 0) return;

            var runIds = await _context.MrpRunLines.AsNoTracking()
                .Where(l => products.Contains(l.ProductCode)
                            && l.MrpRun!.Status == MrpRunStatus.Executed
                            && l.MrpRun.PlantId == plantId)
                .Select(l => l.MrpRunId)
                .Distinct()
                .ToListAsync();

            if (runIds.Count == 0) return;
            var runs = await _context.MrpRuns
                .Where(r => runIds.Contains(r.MrpRunId) && !r.IsDirty)
                .ToListAsync();
            foreach (var r in runs) r.IsDirty = true;
            if (runs.Count > 0) await _context.SaveChangesAsync();
        }
    }
}
