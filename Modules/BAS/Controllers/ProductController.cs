using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class ProductController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ProductMaster";

        public ProductController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IUomService uomService, ICurrencyService currencyService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _uomService = uomService;
            _currencyService = currencyService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }


        // GET: /Product
        public async Task<IActionResult> Index(
            string? searchTerm, string? stockMode,
            int? pageNumber, int? pageSize, string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm      = searchTerm;
            ViewBag.StockMode       = stockMode;
            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort     = sortOrder;
            ViewBag.CodeSort  = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort  = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            // 面板表單（_ProductForm）的 onStockUomChange/applyUomStep 需要小數位對照。
            // 因表單 partial 經 DOMPurify.sanitize 注入會被剝除 <script>，故 window.uomDecimals
            // 與面板互動 handler 一律改在 Index @section Scripts（主文件、不經 sanitize）提供。
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

            // Populate StockMode options from CodeEntry
            var stockModeEntries = await _context.CodeEntries
                .AsNoTracking()
                .Where(e => e.Category == "stock_mode" && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .ToDisplayOptionsAsync();
            ViewBag.StockModes = stockModeEntries;
            ViewBag.StockModeNames = stockModeEntries.ToDictionary(e => e.Code, e => e.DisplayName);

            if (!searched) return View((PaginatedList<Product>?)null);

            var query = _context.Products.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(p =>
                    p.ProductCode.ToLower().Contains(lower) ||
                    p.ProductName.ToLower().Contains(lower) ||
                    (p.Spec != null && p.Spec.ToLower().Contains(lower)));
            }

            if (!string.IsNullOrEmpty(stockMode))
                query = query.Where(p => p.StockMode == stockMode);

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(p => p.ProductCode),
                "name_asc"  => query.OrderBy(p => p.ProductName),
                "name_desc" => query.OrderByDescending(p => p.ProductName),
                _           => query.OrderBy(p => p.ProductCode),
            };

            return View(await PaginatedList<Product>.CreateAsync(query, pageNumber ?? 1, pageSize ?? 20));
        }

        private void ValidateAndNormalizeCategories(Product model, ref string[]? selectedCategories)
        {
            var tags = selectedCategories?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();

            if (string.IsNullOrWhiteSpace(model.CategoryCode))
            {
                ModelState.AddModelError(nameof(model.CategoryCode), _localizer["OperationFailed"].Value);
            }
            else if (!tags.Contains(model.CategoryCode))
            {
                tags.Add(model.CategoryCode);
            }

            if (tags.Count == 0)
                ModelState.AddModelError(nameof(model.CategoryCode), _localizer["OperationFailed"].Value);

            selectedCategories = tags.Distinct().ToArray();
        }

        // 驗證 Product.RoutingId 指向的途程存在、且其 ProductCode 等於本品號
        // （mfg-domain.md §3.1(1)：預設途程必須是該品號的途程；不支援跨品號共用）
        private async Task ValidateRoutingConsistencyAsync(Product model)
        {
            if (string.IsNullOrWhiteSpace(model.RoutingId)) return;

            var routing = await _context.Set<SmartFactory.MES.Modules.MFG.Models.Routing>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.RoutingId == model.RoutingId);

            if (routing == null)
            {
                ModelState.AddModelError(nameof(model.RoutingId), _localizer["RecordNotFound"].Value);
            }
            // 通用途程（ProductCode 空白）可被任意品號採用；僅品號專屬途程才要求相符（mfg-domain.md §3.1(1)）
            else if (!string.IsNullOrWhiteSpace(routing.ProductCode) &&
                     !string.Equals(routing.ProductCode, model.ProductCode, StringComparison.Ordinal))
            {
                ModelState.AddModelError(nameof(model.RoutingId),
                    PzText.Get("Bas_RoutingProductMismatchFmt", model.RoutingId, routing.ProductCode, model.ProductCode));
            }
        }

        private async Task PrepareFormDropdowns(List<string>? selectedTags = null)
        {
            ViewBag.UomOptions = await _context.Uoms
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            ViewBag.UomDecimals    = await _uomService.GetAllDecimalPlacesAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            ViewBag.CategoryOptions = await _context.ProductCategories
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CategoryCode)
                .Select(c => new { c.CategoryCode, c.CategoryName, c.EnableLotTracking,
                                   c.DefaultWarehouseCode, c.DefaultLocationCode, c.DefaultStockUomCode })
                .ToListAsync();

            ViewBag.AllCategories = await _context.ProductCategories
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CategoryCode)
                .Select(c => new { c.CategoryCode, c.CategoryName })
                .ToListAsync();

            // 預設途程代號 select2 來源（mfg-domain.md §3.1(1)）：列出本廠有效途程
            ViewBag.Routings = await _context.Set<SmartFactory.MES.Modules.MFG.Models.Routing>()
                .AsNoTracking()
                .Where(r => r.IsActive)
                .OrderBy(r => r.RoutingId)
                .Select(r => new { r.RoutingId, r.RoutingName })
                .ToListAsync();

            ViewBag.SelectedCategories = selectedTags ?? new List<string>();
        }

        // GET: /Product/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            var model = new Product { IsActive = true };
            if (IsAjax) return PartialView("_ProductForm", model);
            return View("_ProductForm", model);
        }


        /// <summary>
        /// 產品類別與三個單位欄位必須存在且未停用（皆為全域主檔，無廠別）。
        ///
        /// <c>ValidateAndNormalizeCategories</c> 只驗「有沒有填」，填了一個已停用或不存在的
        /// 代碼照樣過關。停用的單位一旦掛上品號，之後所有數量欄位的小數位都會取不到設定，
        /// 而那是靜默退回預設值，不是錯誤。
        /// </summary>
        private async Task ValidateReferencesAsync(Product model)
        {
            AddRefError(nameof(model.CategoryCode),
                await MasterReferenceGuard.CheckProductCategoryAsync(_context, model.CategoryCode), model.CategoryCode);
            AddRefError(nameof(model.StockUom),
                await MasterReferenceGuard.CheckUomAsync(_context, model.StockUom), model.StockUom);
            AddRefError(nameof(model.SalesUom),
                await MasterReferenceGuard.CheckUomAsync(_context, model.SalesUom), model.SalesUom);
            AddRefError(nameof(model.PurchaseUom),
                await MasterReferenceGuard.CheckUomAsync(_context, model.PurchaseUom), model.PurchaseUom);
        }

        private void AddRefError(string field, MasterRefStatus status, string? code)
        {
            var key = MasterReferenceGuard.MessageKey(status);
            if (key == null) return;
            ModelState.AddModelError(field, string.Format(_localizer[key].Value, code));
        }

        // POST: /Product/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Product.ProductCode), nameof(Product.ProductName), nameof(Product.Spec),
            nameof(Product.RoutingId), nameof(Product.SalesUom), nameof(Product.PurchaseUom),
            nameof(Product.StockUom), nameof(Product.StockMode), nameof(Product.SafetyStock),
            nameof(Product.StandardCost), nameof(Product.MrpMode), nameof(Product.LeadTimeDays),
            nameof(Product.UrgentLeadTimeDays), nameof(Product.OutsourceQtyPerDay), nameof(Product.ShelfLifeDays),
            nameof(Product.NetWeight), nameof(Product.GrossWeight), nameof(Product.BoxQty),
            nameof(Product.IsLotControlled), nameof(Product.InspectionLevelN), nameof(Product.InspectionLevelR),
            nameof(Product.InspectionLevelC), nameof(Product.Remarks), nameof(Product.CategoryCode),
            nameof(Product.IsActive), nameof(Product.MinLotSize), nameof(Product.LotMultiple))] Product model, string[]? selectedCategories)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ValidateAndNormalizeCategories(model, ref selectedCategories);
            await ValidateRoutingConsistencyAsync(model);
            await ValidateReferencesAsync(model);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns(selectedCategories?.ToList());
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ProductForm", model);
                return View("_ProductForm", model);
            }

            var exists = await _context.Products.AnyAsync(p => p.ProductCode == model.ProductCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.ProductCode), _localizer["DuplicateCode"].Value);
                await PrepareFormDropdowns(selectedCategories?.ToList());
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ProductForm", model);
                return View("_ProductForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            // 銷售/採購單位空白時回填為庫存單位（StockUom 為換算基準）
            if (string.IsNullOrWhiteSpace(model.SalesUom))    model.SalesUom    = model.StockUom;
            if (string.IsNullOrWhiteSpace(model.PurchaseUom)) model.PurchaseUom = model.StockUom;

            // 依 StockUom 小數位數四捨五入相關數量欄位
            model.SafetyStock        = await _uomService.RoundQtyAsync(model.StockUom, model.SafetyStock);
            model.OutsourceQtyPerDay = await _uomService.RoundQtyAsync(model.StockUom, model.OutsourceQtyPerDay);
            if (model.BoxQty.HasValue)
                model.BoxQty = await _uomService.RoundQtyAsync(model.StockUom, model.BoxQty.Value);

            if (selectedCategories != null)
            {
                model.Tags = selectedCategories
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => new ProductCategoryTag { ProductCode = model.ProductCode, CategoryCode = c })
                    .ToList();
            }

            var config = await _approvalService.GetApprovalConfig("Product", _userContext.PlantId);
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                model.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                model.IsActive = false;
                _context.Products.Add(model);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                    return StatusCode(500);
                }
                var request = await _approvalService.SubmitForApproval(
                    model.ProductCode, "Product", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                model.ApprovalRequestId = request.Id;
                await _context.SaveChangesAsync();
                if (IsAjax) return Json(new { success = true, message = _localizer["SubmittedForApprovalCreate"].Value });
            }
            else
            {
                model.ApprovalStatus = DocumentApprovalStatus.Approved;
                _context.Products.Add(model);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            }
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Product/Details/{code}  — 唯讀檢視（供簽核頁面 Side Panel 使用）
        public async Task<IActionResult> Details(string code)
        {
            if (!await CanReadDocumentAsync(MenuKey, code)) return Forbid();
            var rec = await _context.Products
                .Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.ProductCode == code);
            if (rec == null) return NotFound();
            await PrepareFormDropdowns(rec.Tags.Select(c => c.CategoryCode).ToList());
            ViewBag.IsEdit = true;
            ViewBag.IsReadOnly = true;
            if (IsAjax) return PartialView("_ProductForm", rec);
            return View("_ProductForm", rec);
        }

        // GET: /Product/Edit/{code}
        [HttpGet]
        public async Task<IActionResult> Edit(string code)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Products
                .Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.ProductCode == code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            await PrepareFormDropdowns(rec.Tags.Select(c => c.CategoryCode).ToList());
            ViewBag.IsEdit = true;

            if (IsAjax) return PartialView("_ProductForm", rec);
            return View("_ProductForm", rec);
        }

        // POST: /Product/Edit/{code}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string code, Product updated, string[]? selectedCategories)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            ValidateAndNormalizeCategories(updated, ref selectedCategories);
            await ValidateRoutingConsistencyAsync(updated);
            await ValidateReferencesAsync(updated);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns(selectedCategories?.ToList());
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ProductForm", updated);
                return View("_ProductForm", updated);
            }

            var rec = await _context.Products
                .Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.ProductCode == code);
            if (rec == null) return NotFound();

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["PendingApprovalCannotEdit"].Value });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            rec.ProductName         = updated.ProductName;
            rec.Spec                = updated.Spec;
            rec.RoutingId           = updated.RoutingId;
            rec.StockUom            = updated.StockUom;
            // 銷售/採購單位空白時回填為庫存單位（StockUom 為換算基準）
            rec.SalesUom            = string.IsNullOrWhiteSpace(updated.SalesUom)    ? updated.StockUom : updated.SalesUom;
            rec.PurchaseUom         = string.IsNullOrWhiteSpace(updated.PurchaseUom) ? updated.StockUom : updated.PurchaseUom;
            rec.StockMode           = updated.StockMode;
            rec.SafetyStock         = await _uomService.RoundQtyAsync(updated.StockUom, updated.SafetyStock);
            rec.StandardCost        = updated.StandardCost;
            rec.MrpMode             = updated.MrpMode;
            rec.LeadTimeDays        = updated.LeadTimeDays;
            rec.UrgentLeadTimeDays  = updated.UrgentLeadTimeDays;
            rec.OutsourceQtyPerDay  = await _uomService.RoundQtyAsync(updated.StockUom, updated.OutsourceQtyPerDay);
            rec.ShelfLifeDays       = updated.ShelfLifeDays;
            rec.NetWeight           = updated.NetWeight;
            rec.GrossWeight         = updated.GrossWeight;
            rec.BoxQty              = updated.BoxQty.HasValue
                ? await _uomService.RoundQtyAsync(updated.StockUom, updated.BoxQty.Value)
                : null;
            rec.IsLotControlled     = updated.IsLotControlled;
            rec.InspectionLevelN    = updated.InspectionLevelN;
            rec.InspectionLevelR    = updated.InspectionLevelR;
            rec.InspectionLevelC    = updated.InspectionLevelC;
            rec.Remarks             = updated.Remarks;
            rec.CategoryCode        = updated.CategoryCode;
            rec.IsActive            = updated.IsActive;
            rec.UpdatedAt           = DateTime.UtcNow;

            // Sync tags
            rec.Tags.Clear();
            if (selectedCategories != null)
            {
                foreach (var cat in selectedCategories.Where(c => !string.IsNullOrWhiteSpace(c)))
                    rec.Tags.Add(new ProductCategoryTag { ProductCode = code, CategoryCode = cat });
            }

            var config = await _approvalService.GetApprovalConfig("Product", _userContext.PlantId);
            string msg;
            if (config?.IsApprovalRequired == true && !string.IsNullOrEmpty(config.ApprovalTemplateId))
            {
                rec.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
                rec.IsActive = false;
                await _context.SaveChangesAsync();
                var request = await _approvalService.SubmitForApproval(
                    code, "Product", config.ApprovalTemplateId,
                    _userContext.UserId ?? string.Empty);
                rec.ApprovalRequestId = request.Id;
                msg = PzText.Get("Bas_ProductResubmitted");
            }
            else
            {
                msg = PzText.Get("Bas_ProductUpdated");
            }

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            if (IsAjax) return Json(new { success = true, message = msg });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Product/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string code)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Products
                .Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.ProductCode == code);
            if (rec == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            if (rec.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["StagingErrDeletePending"].Value });
                TempData["ErrorMessage"] = _localizer["PendingApprovalCannotEdit"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            // 途程主檔/途程用料、庫存餘額皆以 ProductCode 鬆散字串參照（無 DB 層 FK 可攔截），
            // 主動查核，避免品號刪除後留下孤兒資料。
            if (await IsProductInUseAsync(code))
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["ProductInUseCannotDelete"].Value });
                TempData["ErrorMessage"] = _localizer["ProductInUseCannotDelete"].Value;
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            _context.Products.Remove(rec);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>品號是否仍被途程（含用料）或庫存餘額引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsProductInUseAsync(string code)
        {
            if (await _context.Routings.IgnoreQueryFilters().AnyAsync(r => r.ProductCode == code))
                return true;
            if (await _context.RoutingBoms.AnyAsync(b => b.ProductCode == code))
                return true;
            if (await _context.Inventories.IgnoreQueryFilters()
                    .AnyAsync(i => i.ProductCode == code && (i.QtyOnHand != 0 || i.QtyReserved != 0)))
                return true;
            return false;
        }

        // GET: /Product/GetCategories?code=XXX
        [HttpGet]
        public async Task<IActionResult> GetCategories(string code)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var cats = await _context.ProductCategoryTags.AsNoTracking()
                .Where(c => c.ProductCode == code)
                .Select(c => c.CategoryCode)
                .ToListAsync();
            return Json(cats);
        }

        // GET: /Product/GetCategoryDefaults?categoryCode=XXX
        [HttpGet]
        public async Task<IActionResult> GetCategoryDefaults(string categoryCode)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var cat = await _context.ProductCategories.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CategoryCode == categoryCode);
            if (cat == null) return NotFound();
            return Json(new {
                enableLotTracking    = cat.EnableLotTracking,
                defaultWarehouseCode = cat.DefaultWarehouseCode,
                defaultLocationCode  = cat.DefaultLocationCode,
                defaultStockUomCode  = cat.DefaultStockUomCode
            });
        }

        // GET: /Product/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null, string? stockMode = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Products.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(p =>
                    p.ProductCode.ToLower().Contains(lower) ||
                    p.ProductName.ToLower().Contains(lower));
            }
            if (!string.IsNullOrEmpty(stockMode))
                query = query.Where(p => p.StockMode == stockMode);

            var data = await query.OrderBy(p => p.ProductCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Product.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["ProductCode"].Value, L["ProductName"].Value, L["Spec"].Value,
                L["SalesUom"].Value, L["PurchaseUom"].Value, L["StockUom"].Value,
                L["StockMode"].Value, L["SafetyStock"].Value, L["StandardCost"].Value,
                L["LeadTimeDays"].Value, L["IsLotControlled"].Value, L["IsActive"].Value
            };
            var rows = data.Select(p => new object?[]
            {
                p.ProductCode, p.ProductName, p.Spec, p.SalesUom, p.PurchaseUom, p.StockUom,
                p.StockMode, p.SafetyStock, p.StandardCost, p.LeadTimeDays, p.IsLotControlled, p.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Product" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Product.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Product.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Product.json"),
            };
        }
    }
}
