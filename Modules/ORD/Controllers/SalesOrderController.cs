using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.ORD.Controllers
{
    [Authorize]
    public class SalesOrderController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IApprovalService _approvalService;
        private readonly IDocNumberService _docNumberService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IInventoryReservationService _reservations;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly Modules.MFG.Services.IMrpAutoExpandService _autoExpand;

        private const string MenuKey = "SalesOrder";
        private const string DocType = "SalesOrder";

        public SalesOrderController(
            ApplicationDbContext context, IUserContext userContext,
            IPermissionService permissionService, IDateTimeService dateTimeService,
            IApprovalService approvalService, IDocNumberService docNumberService,
            IUomService uomService, ICurrencyService currencyService,
            IInventoryReservationService reservations,
            IStringLocalizer<ProjectZSharedResource> localizer,
            Modules.MFG.Services.IMrpAutoExpandService autoExpand)
        {
            _context = context; _userContext = userContext;
            _permissionService = permissionService; _dateTimeService = dateTimeService;
            _approvalService = approvalService; _docNumberService = docNumberService;
            _uomService = uomService; _currencyService = currencyService;
            _reservations = reservations;
            _autoExpand = autoExpand;

            _localizer = localizer;
        }

        /// <summary>
        /// §25.5：訂單進入「已核准」後，簡化模式自動展開 MRP。
        /// 服務以狀態判定（重算未展開剩餘量），非簡化模式或無未展開需求時不做事，
        /// 因此這裡只需在「本次儲存/送審讓單據變成已核准」時呼叫，不必逐條核准路徑掛鉤。
        /// 展開失敗不影響訂單本身已儲存的事實，改以訊息提示。
        /// </summary>
        private async Task<IActionResult> SavedWithAutoExpandAsync(string? message = null)
        {
            var r = await _autoExpand.ExpandApprovedOrdersAsync();
            if (r.Triggered)
                message = r.Error == null
                    ? PzText.Get("Ord_SavedMrpExpandedFmt", r.MrpRunId)
                    : PzText.Get("Ord_SavedMrpFailedFmt", r.Error, r.MrpRunId);

            var toast = message == null
                ? "window.toastSuccess&&window.toastSuccess();"
                : $"window.toastSuccess&&window.toastSuccess({System.Text.Json.JsonSerializer.Serialize(message)});";
            return Content($"<script>{toast}refreshList&&refreshList();</script>", "text/html");
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        /// <summary>
        /// 後端權威解析每行 ExchangeRate：依 CurrencyCode + documentDate 查 ≤ documentDate 最近一筆匯率。
        /// ORD 用賣出匯率（公司收外幣客戶款項）。找不到時保留 vm 原值（前端預設 1）。
        /// </summary>
        private async Task ResolveLineRatesAsync(IEnumerable<SalesOrderLineVm> lines, DateOnly documentDate)
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

        /// <summary>
        /// 後端權威覆寫 StockQty / StockUomCode（不採信前端傳入值）。
        ///
        /// 這兩欄原本是前端送什麼存什麼——與出貨單
        /// （<c>GoodsShipmentController.ApplyLinesAsync</c> 以品號主檔 StockUom 加
        /// <see cref="IUomService"/> 重算）不對稱。單純顯示時這個差異看不出來，
        /// 但累計出貨的守門改以庫存基準單位比對之後，<c>StockQty</c> 就成了
        /// 「這張訂單到底訂了多少」的權威值——建立在可竄改的前端輸入上等於沒有守門。
        /// </summary>
        private async Task ResolveLineStockQtyAsync(IEnumerable<SalesOrderLineVm> lines)
        {
            var productCodes = lines.Select(l => l.ProductCode).Distinct().ToList();
            var stockUomMap = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom);

            foreach (var lv in lines)
            {
                var stockUom = stockUomMap.TryGetValue(lv.ProductCode, out var su) && !string.IsNullOrEmpty(su)
                    ? su!
                    : (!string.IsNullOrEmpty(lv.StockUomCode) ? lv.StockUomCode! : lv.UomCode ?? "");
                var stockQty = string.IsNullOrEmpty(lv.UomCode) || lv.UomCode == stockUom
                    ? lv.OrderQty
                    : await _uomService.ConvertAsync(lv.ProductCode, lv.UomCode!, stockUom, lv.OrderQty);

                lv.StockUomCode = stockUom;
                lv.StockQty     = string.IsNullOrEmpty(stockUom)
                    ? stockQty
                    : await _uomService.RoundQtyAsync(stockUom, stockQty);
            }
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
                .OrderBy(c => c.CompanyCode)
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

            ViewBag.OrderTypeOptions = await _context.CodeEntries
                .AsNoTracking().Where(e => e.Category == "PZ.SalesOrderType" && e.IsActive)
                .OrderBy(e => e.SortOrder).ToDisplayOptionsAsync();

            // 貿易條件（Incoterms）。與採購單同一個代碼類別——同一組 Incoterms 兩邊通用，
            // 各開一份只會讓兩邊慢慢長出不同的代碼。
            ViewBag.TradeTermOptions = await _context.CodeEntries
                .AsNoTracking().Where(e => e.Category == "trade_term" && e.IsActive)
                .OrderBy(e => e.SortOrder).ToDisplayOptionsAsync();

            ViewBag.DepartmentOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            ViewBag.UomDecimals      = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            // 明細本幣金額（AmountLocal）以本位幣代號與精度顯示、加總
            ViewBag.BaseCurrency     = await _currencyService.GetBaseCurrencyAsync() ?? "";
            ViewBag.BaseCurrencyDp   = await ResolveBaseCurrencyDpAsync();
            ViewBag.CanPrint         = await _permissionService.CanPrint(MenuKey);
        }

        public async Task<IActionResult> Index(
            string? searchTerm, string? companyCode,
            DateOnly? dateFrom, DateOnly? dateTo,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var plantId = _userContext.PlantId;
            ViewBag.SearchTerm      = searchTerm;
            ViewBag.CompanyCode     = companyCode;
            ViewBag.DateFrom        = dateFrom;
            ViewBag.DateTo          = dateTo;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.IdSort          = string.IsNullOrEmpty(sortOrder) || sortOrder == "id_asc" ? "id_desc" : "id_asc";
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            await PrepareIndexPermissionsAsync(MenuKey);
            ViewBag.BaseCurrencyDp  = await ResolveBaseCurrencyDpAsync();
            ViewBag.CustomerOptions = await _context.Companies
                .AsNoTracking().Where(c => c.IsCustomer && c.IsActive).OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName }).ToListAsync();

            if (!searched) return View((PaginatedList<SalesOrder>?)null);

            var query = _context.SalesOrders.AsNoTracking()
                .Include(so => so.Lines).Where(so => so.PlantId == plantId);

            if (!string.IsNullOrWhiteSpace(searchTerm))
                query = query.Where(so => so.SalesOrderId.Contains(searchTerm)
                    || (so.CustomerOrderNo != null && so.CustomerOrderNo.Contains(searchTerm))
                    || (so.Remarks != null && so.Remarks.Contains(searchTerm)));
            if (!string.IsNullOrWhiteSpace(companyCode))
                query = query.Where(so => so.CompanyCode == companyCode);
            if (dateFrom.HasValue) query = query.Where(so => so.OrderDate >= dateFrom.Value);
            if (dateTo.HasValue)   query = query.Where(so => so.OrderDate <= dateTo.Value);

            query = sortOrder switch
            {
                "id_asc"    => query.OrderBy(so => so.SalesOrderId),
                "id_desc"   => query.OrderByDescending(so => so.SalesOrderId),
                "date_asc"  => query.OrderBy(so => so.OrderDate),
                "date_desc" => query.OrderByDescending(so => so.OrderDate),
                _           => query.OrderByDescending(so => so.SalesOrderId),
            };
            return View(await PaginatedList<SalesOrder>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            var vm = new SalesOrderFormVm { CreatedBy = _userContext.UserId ?? "", OrderDate = _dateTimeService.GetLocalDate() };
            return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return NotFound();
            await SyncApprovalStatusAsync(so); await PrepareFormDropdowns();
            var vm = MapToVm(so); return IsAjax ? PartialView("_Form", vm) : RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id, DocType)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return NotFound();
            await PrepareFormDropdowns(); ViewBag.IsReadOnly = true;
            return IsAjax ? PartialView("_Form", MapToVm(so)) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(SalesOrderFormVm vm)
        {
            if (string.IsNullOrEmpty(vm.SalesOrderId)
                ? !await _permissionService.CanCreate(MenuKey)
                : !await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid) { await PrepareFormDropdowns(); return PartialView("_Form", vm); }
            try
            {
                var userId = _userContext.UserId ?? "";
                var plantId = _userContext.PlantId ?? "";
                var targetStatus = await _approvalService.ResolveSaveStatusAsync(DocType, plantId);

                // 後端權威覆寫 ExchangeRate（避免前端傳入過期或被竄改的匯率）
                await ResolveLineRatesAsync(vm.Lines, vm.OrderDate);
                // 同理覆寫 StockQty / StockUomCode：累計出貨守門以庫存基準單位比對，
                // 基準值必須由伺服端算出（見該方法註解）。
                await ResolveLineStockQtyAsync(vm.Lines);

                if (string.IsNullOrEmpty(vm.SalesOrderId))
                {
                    var so = new SalesOrder
                    {
                        SalesOrderId    = await _docNumberService.NextAsync(DocType, plantId, vm.OrderDate),
                        OrderDate       = vm.OrderDate,
                        OrderType       = vm.OrderType,
                        TradeTermCode   = vm.TradeTermCode,
                        CompanyCode     = vm.CompanyCode,
                        DepartmentCode  = vm.DepartmentCode,
                        CustomerOrderNo = vm.CustomerOrderNo,
                        TaxCode         = vm.TaxCode,
                        Remarks         = vm.Remarks,
                        CreatedBy       = userId,
                        ApprovalStatus  = targetStatus,
                    };
                    ApplyLines(so, vm); _context.SalesOrders.Add(so);
                }
                else
                {
                    var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == vm.SalesOrderId)
                        ?? throw new InvalidOperationException(PzText.Get("Ord_SalesOrderNotFound"));
                    // xmin 樂觀鎖：以前端帶回的版本作為 OriginalValue（見 ord-module.md §13）
                    _context.Entry(so).Property(e => e.xmin).OriginalValue = vm.RowVersion;
                    if (so.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                        throw new InvalidOperationException(PzText.Get("Ord_SalesOrderPendingApproval"));
                    if (so.Lines.Any(l => l.ShippedQty > 0 || l.ReturnedQty > 0))
                        throw new InvalidOperationException(PzText.Get("Ord_HasShipmentsOrReturns"));

                    // §21.2：已匯入 MRP 且有投產工單時阻擋修改
                    var mrpWarning = await CheckMrpImpactAsync(vm.SalesOrderId);
                    if (mrpWarning.HasInProgressWo)
                        throw new InvalidOperationException(PzText.Get("Ord_WorkOrderInProgress"));

                    // §21.7（縮量反向流）：記錄修改前總量，供稍後判斷是否縮量 → 釋放殘留預留
                    var oldTotalQty = so.Lines.Sum(l => l.OrderQty);

                    so.OrderDate = vm.OrderDate; so.OrderType = vm.OrderType;
                    so.TradeTermCode = vm.TradeTermCode;
                    so.CompanyCode = vm.CompanyCode; so.DepartmentCode = vm.DepartmentCode;
                    so.CustomerOrderNo = vm.CustomerOrderNo;
                    so.TaxCode = vm.TaxCode; so.Remarks = vm.Remarks; so.UpdatedAt = DateTime.UtcNow;
                    so.ApprovalStatus = targetStatus;
                    UpdateLines(_context, so, vm);

                    await _context.SaveChangesAsync();
                    // §21.2：有 MRP 展開但無投產工單 → 附帶 warning
                    if (mrpWarning.HasMrpLines)
                    {
                        // §21.6：標記關聯 MrpRun 為 dirty
                        await MarkRelatedMrpRunsDirtyAsync(vm.SalesOrderId!);
                        // §21.7：縮量 → 立即釋放殘留預留（避免殭屍超量；MRP 重跑會以新量重建）
                        var newTotalQty = so.Lines.Sum(l => l.OrderQty);
                        if (newTotalQty < oldTotalQty)
                            await ReleaseMrpReservationsForSoAsync(vm.SalesOrderId!);
                        return Content("<script>window.toastSuccess&&window.toastSuccess(" + System.Text.Json.JsonSerializer.Serialize(PzText.Get("Ord_SavedSuggestMrpRerun")) + ");refreshList&&refreshList();</script>", "text/html");
                    }

                    return targetStatus == DocumentApprovalStatus.Approved
                        ? await SavedWithAutoExpandAsync()
                        : Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
                }
                await _context.SaveChangesAsync();
                return targetStatus == DocumentApprovalStatus.Approved
                    ? await SavedWithAutoExpandAsync()
                    : Content("<script>window.toastSuccess&&window.toastSuccess();refreshList&&refreshList();</script>", "text/html");
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, _localizer["ConcurrencyConflict"].Value);
                var latest = await _context.SalesOrders.AsNoTracking().Include(s => s.Lines)
                    .FirstOrDefaultAsync(s => s.SalesOrderId == vm.SalesOrderId);
                await PrepareFormDropdowns();
                return PartialView("_Form", latest == null ? vm : MapToVm(latest));
            }
            catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)
            { ModelState.AddModelError(string.Empty, ex.Message); await PrepareFormDropdowns(); return PartialView("_Form", vm); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(string id)
        {
            var result = await SubmitForApprovalAsync<SalesOrder>(id, MenuKey, DocType);
            // 無簽核流程／自動跳簽時，送審即終局核准 → 同樣要自動展開（§25.5）。
            // 服務以狀態判定，仍在待審核時不會有未展開需求變化，呼叫是安全的。
            if (result is OkObjectResult) await _autoExpand.ExpandApprovedOrdersAsync();
            return result;
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(string id)
            => await WithdrawApprovalAsync<SalesOrder>(id, MenuKey);

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
            if (so.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
            if (so.Lines.Any(l => l.ShippedQty > 0))
                return Json(new { success = false, message = _localizer["HasShipmentCannotDelete"].Value });
            // 已納入 MRP 執行單（草稿或已展開）的訂單不可刪除，否則需求列懸空、
            // 展開的生產批/工單失去需求來源（mfg-domain.md §11）
            var mrpRunIds = await _context.MrpRunLines
                .Where(l => l.SalesOrderId == id)
                .Select(l => l.MrpRunId).Distinct().OrderBy(x => x).ToListAsync();
            if (mrpRunIds.Count > 0)
                return Json(new { success = false, message = string.Format(
                    _localizer["OrderReferencedByMrpCannotDelete"].Value, string.Join(", ", mrpRunIds)) });
            _context.SalesOrders.Remove(so); await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> OpenLines(string id)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var plantId = _userContext.PlantId;
            var lines = await _context.SalesOrderLines
                .AsNoTracking().Include(l => l.SalesOrder)
                .Where(l => l.SalesOrderId == id && l.SalesOrder!.PlantId == plantId
                    && l.SalesOrder.ApprovalStatus == DocumentApprovalStatus.Approved
                    && !l.IsClosed && l.OrderQty > l.ShippedQty)
                .OrderBy(l => l.LineNo)
                .Select(l => new
                {
                    l.LineNo, l.ProductCode, l.DeliveryDate,
                    l.OrderQty, l.UomCode, l.StockQty, l.StockUomCode,
                    l.UnitPrice, l.CurrencyCode, l.ExchangeRate,
                    l.TaxCode, l.TaxRate, l.ShippedQty,
                    RemainingQty = l.OrderQty - l.ShippedQty,
                }).ToListAsync();
            return Json(lines);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.SalesOrders.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.SalesOrderId == id && d.PlantId == _userContext.PlantId);
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

        private static SalesOrderFormVm MapToVm(SalesOrder so) => new()
        {
            SalesOrderId    = so.SalesOrderId,
            ApprovalStatus  = so.ApprovalStatus,
            RowVersion      = so.xmin,
            OrderDate       = so.OrderDate,
            CreatedBy       = so.CreatedBy,
            OrderType       = so.OrderType,
            TradeTermCode   = so.TradeTermCode,
            CompanyCode     = so.CompanyCode,
            DepartmentCode  = so.DepartmentCode,
            CustomerOrderNo = so.CustomerOrderNo,
            TaxCode         = so.TaxCode,
            Remarks         = so.Remarks,
            Lines = so.Lines.OrderBy(l => l.LineNo).Select(l => new SalesOrderLineVm
            {
                LineNo = l.LineNo, ProductCode = l.ProductCode, DeliveryDate = l.DeliveryDate,
                OrderQty = l.OrderQty, UomCode = l.UomCode, StockQty = l.StockQty, StockUomCode = l.StockUomCode,
                UnitPrice = l.UnitPrice, CurrencyCode = l.CurrencyCode, ExchangeRate = l.ExchangeRate,
                TaxCode = l.TaxCode, TaxRate = l.TaxRate,
                LineAmount = l.LineAmount, TaxAmount = l.TaxAmount, TotalAmount = l.TotalAmount, AmountLocal = l.AmountLocal,
                IsClosed = l.IsClosed, Remarks = l.Remarks, ShippedQty = l.ShippedQty, ReturnedQty = l.ReturnedQty,
            }).ToList()
        };

        private static void ApplyLines(SalesOrder so, SalesOrderFormVm vm)
        {
            int seq = 1;
            foreach (var lv in vm.Lines)
                so.Lines.Add(new SalesOrderLine
                {
                    SalesOrderId = so.SalesOrderId, LineNo = seq++,
                    ProductCode  = lv.ProductCode, DeliveryDate = lv.DeliveryDate,
                    OrderQty     = lv.OrderQty, UomCode = lv.UomCode,
                    StockQty     = lv.StockQty, StockUomCode = lv.StockUomCode,
                    UnitPrice    = lv.UnitPrice, CurrencyCode = lv.CurrencyCode, ExchangeRate = lv.ExchangeRate,
                    TaxCode      = lv.TaxCode, TaxRate = lv.TaxRate,
                    LineAmount   = lv.LineAmount, TaxAmount = lv.TaxAmount, TotalAmount = lv.TotalAmount, AmountLocal = lv.AmountLocal,
                    IsClosed     = lv.IsClosed, Remarks = lv.Remarks,
                });
            so.TotalAmount = so.Lines.Sum(l => l.AmountLocal);
        }

        /// <summary>
        /// 差異更新明細（修改時用）
        /// ⚠️ Critical：有累計欄位（ShippedQty / ReturnedQty）的 Line 不可整批刪除，必須差異更新
        /// </summary>
        private void UpdateLines(ApplicationDbContext context, SalesOrder so, SalesOrderFormVm vm)
        {
            var dbLines = so.Lines.OrderBy(l => l.LineNo).ToList();
            var vmLines = vm.Lines;

            // Step 1：刪除前端不再有的行（但須檢查 ShippedQty）
            var toDelete = dbLines
                .Where(db => !vmLines.Any(vm => vm.LineNo == db.LineNo))
                .ToList();
            foreach (var line in toDelete)
            {
                if (line.ShippedQty > 0 || line.ReturnedQty > 0)
                    throw new InvalidOperationException(
                        PzText.Get("Ord_LineShippedCannotDeleteFmt", line.LineNo));
                context.SalesOrderLines.Remove(line);
            }

            // Step 2：新增或更新前端傳來的行
            int seq = 1;
            foreach (var lv in vmLines)
            {
                var existing = dbLines.FirstOrDefault(db => db.LineNo == seq);
                if (existing == null)
                {
                    // 新增行
                    so.Lines.Add(new SalesOrderLine
                    {
                        SalesOrderId = so.SalesOrderId,
                        LineNo       = seq,
                        ProductCode  = lv.ProductCode,
                        DeliveryDate = lv.DeliveryDate,
                        OrderQty     = lv.OrderQty,
                        UomCode      = lv.UomCode,
                        StockQty     = lv.StockQty,
                        StockUomCode = lv.StockUomCode,
                        UnitPrice    = lv.UnitPrice,
                        CurrencyCode = lv.CurrencyCode,
                        ExchangeRate = lv.ExchangeRate,
                        TaxCode      = lv.TaxCode,
                        TaxRate      = lv.TaxRate,
                        LineAmount   = lv.LineAmount,
                        TaxAmount    = lv.TaxAmount,
                        TotalAmount  = lv.TotalAmount,
                        AmountLocal  = lv.AmountLocal,
                        IsClosed     = lv.IsClosed,
                        Remarks      = lv.Remarks,
                        // ShippedQty / ReturnedQty：新行初值為 0（由 EF 自動）
                    });
                }
                else
                {
                    // 更新行（保留累計欄位）
                    existing.ProductCode  = lv.ProductCode;
                    existing.DeliveryDate = lv.DeliveryDate;
                    existing.OrderQty     = lv.OrderQty;
                    existing.UomCode      = lv.UomCode;
                    existing.StockQty     = lv.StockQty;
                    existing.StockUomCode = lv.StockUomCode;
                    existing.UnitPrice    = lv.UnitPrice;
                    existing.CurrencyCode = lv.CurrencyCode;
                    existing.ExchangeRate = lv.ExchangeRate;
                    existing.TaxCode      = lv.TaxCode;
                    existing.TaxRate      = lv.TaxRate;
                    existing.LineAmount   = lv.LineAmount;
                    existing.TaxAmount    = lv.TaxAmount;
                    existing.TotalAmount  = lv.TotalAmount;
                    existing.AmountLocal  = lv.AmountLocal;
                    existing.IsClosed     = lv.IsClosed;
                    existing.Remarks      = lv.Remarks;
                    // ⚠️ ShippedQty / ReturnedQty 不更新（這些只能由出貨單累加）
                }
                seq++;
            }

            so.TotalAmount = so.Lines.Sum(l => l.AmountLocal);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseLine(string id, int lineNo)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return NotFound();
            if (so.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            var line = so.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound();
            line.IsClosed = true;
            await _context.SaveChangesAsync();
            // §21.1：釋放此訂單列對應的 MRP 庫存預留
            await ReleaseMrpReservationsForSoLineAsync(id, lineNo);
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ReopenLine(string id, int lineNo)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return NotFound();
            if (so.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["ReopenErrNeedApproval"].Value });
            var line = so.Lines.FirstOrDefault(l => l.LineNo == lineNo);
            if (line == null) return NotFound();
            line.IsClosed = false;
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseDoc(string id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SalesOrderId == id);
            if (so == null) return NotFound();
            if (so.ApprovalStatus != DocumentApprovalStatus.Approved)
                return BadRequest(new { success = false, message = _localizer["CloseErrNeedApproval"].Value });
            foreach (var l in so.Lines.Where(l => !l.IsClosed))
                l.IsClosed = true;
            await _context.SaveChangesAsync();
            // §21.1：釋放此訂單所有列的 MRP 庫存預留
            await ReleaseMrpReservationsForSoAsync(id);
            return Ok(new { success = true });
        }

        /// <summary>
        /// §21.2：查詢此 SO 是否已匯入 MRP，以及是否有投產中的工單。
        /// HasInProgressWo=true → 阻擋修改；HasMrpLines=true, HasInProgressWo=false → 警示。
        /// </summary>
        private async Task<(bool HasMrpLines, bool HasInProgressWo)> CheckMrpImpactAsync(string salesOrderId)
        {
            var mrpLineExists = await _context.MrpRunLines.AsNoTracking()
                .AnyAsync(l => l.SalesOrderId == salesOrderId);
            if (!mrpLineExists) return (false, false);

            // 有投產（InProgress）或更晚狀態的工單 → 阻擋
            var batchIds = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId && l.ProductionBatchId != null)
                .Select(l => l.ProductionBatchId!)
                .Distinct()
                .ToListAsync();

            var hasInProgress = batchIds.Count > 0 && await _context.WorkOrders.AsNoTracking()
                .AnyAsync(w => batchIds.Contains(w.ProductionBatchId)
                            && (w.Status == WorkOrderStatus.InProgress
                             || w.Status == WorkOrderStatus.Completed
                             || w.Status == WorkOrderStatus.Closed));

            return (true, hasInProgress);
        }

        /// <summary>§21.6：標記此 SO 關聯的所有 MrpRun 為 dirty。</summary>
        private async Task MarkRelatedMrpRunsDirtyAsync(string salesOrderId)
        {
            var runIds = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId)
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

        /// <summary>§21.1：釋放此 SO 下所有 MrpRunLine 的庫存預留。</summary>
        private async Task ReleaseMrpReservationsForSoAsync(string salesOrderId)
        {
            var mrpLines = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId)
                .Select(l => new { l.MrpRunId, l.LineSeq })
                .ToListAsync();

            foreach (var ml in mrpLines)
                await _reservations.ReleaseBySourceLineAsync(
                    ReservationSourceType.MrpRunLine, ml.MrpRunId, ml.LineSeq);
        }

        /// <summary>§21.1：釋放此 SO 特定列的 MrpRunLine 庫存預留。</summary>
        private async Task ReleaseMrpReservationsForSoLineAsync(string salesOrderId, int lineNo)
        {
            var mrpLines = await _context.MrpRunLines.AsNoTracking()
                .Where(l => l.SalesOrderId == salesOrderId && l.SalesOrderLineSeq == lineNo)
                .Select(l => new { l.MrpRunId, l.LineSeq })
                .ToListAsync();

            foreach (var ml in mrpLines)
                await _reservations.ReleaseBySourceLineAsync(
                    ReservationSourceType.MrpRunLine, ml.MrpRunId, ml.LineSeq);
        }

    }
}
