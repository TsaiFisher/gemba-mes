using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers
{
    /// <summary>
    /// 廠商單價目錄（pur-domain.md §2）— 全域主檔，採購單建行時自動帶入議定單價。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class VendorPriceListController : ModuleControllerBase
    {
        private const string MenuKey = "VendorPriceList";

        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ICurrencyService _currency;
        private readonly IDateTimeService _dt;

        public VendorPriceListController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            ICurrencyService currency,
            IDateTimeService dt)
        {
            _localizer = localizer;
            _currency  = currency;
            _dt        = dt;
        }

        // GET /VendorPriceList
        public async Task<IActionResult> Index(
            string? supplierCode, string? productCode, string? currencyCode,
            bool activeOnly = true, bool searched = false,
            string sortOrder = "supplier_asc", int pageNumber = 1, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.SupplierCode  = supplierCode;
            ViewBag.ProductCode   = productCode;
            ViewBag.CurrencyCode  = currencyCode;
            ViewBag.ActiveOnly    = activeOnly;
            ViewBag.CurrentSort   = sortOrder;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            await PrepareDropdowns();
            ViewBag.ProductOptions = await Db.Products
                .AsNoTracking().Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();
            if (!searched) return View((PaginatedList<VendorPriceList>?)null);

            var query = Db.VendorPriceLists.AsNoTracking()
                .Include(v => v.Supplier)
                .Include(v => v.Product)
                .Include(v => v.Currency)
                .AsQueryable();

            if (!string.IsNullOrEmpty(supplierCode))
                query = query.Where(v => v.SupplierCode == supplierCode);
            if (!string.IsNullOrEmpty(productCode))
                query = query.Where(v => v.ProductCode == productCode);
            if (!string.IsNullOrEmpty(currencyCode))
                query = query.Where(v => v.CurrencyCode == currencyCode);
            if (activeOnly)
                query = query.Where(v => v.IsActive);

            query = sortOrder switch
            {
                "supplier_desc" => query.OrderByDescending(v => v.SupplierCode).ThenBy(v => v.ProductCode),
                "product_asc"   => query.OrderBy(v => v.ProductCode).ThenBy(v => v.SupplierCode),
                "product_desc"  => query.OrderByDescending(v => v.ProductCode).ThenBy(v => v.SupplierCode),
                "valid_from"    => query.OrderBy(v => v.ValidFrom),
                "valid_from_desc" => query.OrderByDescending(v => v.ValidFrom),
                _               => query.OrderBy(v => v.SupplierCode).ThenBy(v => v.ProductCode)
            };

            var list = await PaginatedList<VendorPriceList>.CreateAsync(query, pageNumber, pageSize);
            return View(list);
        }

        // GET /VendorPriceList/Create (AJAX side panel)
        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            var vm = new VendorPriceList { ValidFrom = _dt.GetLocalDate(), IsActive = true };
            await PrepareFormDropdowns(vm);
            return IsAjax ? PartialView("_Form", vm) : View("_Form", vm);
        }

        // GET /VendorPriceList/Edit/{id}
        public async Task<IActionResult> Edit(int id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var item = await Db.VendorPriceLists.FindAsync(id);
            if (item == null) return NotFound();
            await PrepareFormDropdowns(item);
            return IsAjax ? PartialView("_Form", item) : View("_Form", item);
        }

        // POST /VendorPriceList/Save
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(VendorPriceList vm)
        {
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns(vm);
                return PartialView("_Form", vm);
            }

            try
            {
                if (vm.Id == 0)
                {
                    if (!await Permissions.CanCreate(MenuKey)) return Forbid();
                    vm.CreatedAt = vm.UpdatedAt = DateTime.UtcNow;
                    Db.VendorPriceLists.Add(vm);
                }
                else
                {
                    if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
                    var existing = await Db.VendorPriceLists.FindAsync(vm.Id);
                    if (existing == null) return NotFound();

                    existing.SupplierCode = vm.SupplierCode;
                    existing.ProductCode  = vm.ProductCode;
                    existing.CurrencyCode = vm.CurrencyCode;
                    existing.UnitPrice    = vm.UnitPrice;
                    existing.MinQty       = vm.MinQty;
                    existing.ValidFrom    = vm.ValidFrom;
                    existing.ValidTo      = vm.ValidTo;
                    existing.IsActive     = vm.IsActive;
                    existing.Remarks      = vm.Remarks;
                    existing.UpdatedAt    = DateTime.UtcNow;
                }

                await Db.SaveChangesAsync();
                return Content(
                    "<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>",
                    "text/html");
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("unique") == true)
            {
                ModelState.AddModelError(string.Empty, _localizer["VendorPriceListDuplicate"].Value);
                await PrepareFormDropdowns(vm);
                return PartialView("_Form", vm);
            }
        }

        // POST /VendorPriceList/Delete/{id}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoPermission"].Value });

            var item = await Db.VendorPriceLists.FindAsync(id);
            if (item == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            Db.VendorPriceLists.Remove(item);
            await Db.SaveChangesAsync();
            return Json(new { success = true });
        }

        // GET /api/pur/vendor-price?supplierCode=&productCode=&orderDate=&currencyCode=
        // 採購單建行時呼叫：取最新有效議定單價
        [HttpGet("/api/pur/vendor-price")]
        public async Task<IActionResult> GetPrice(
            string supplierCode, string productCode, string currencyCode, DateOnly? orderDate)
        {
            // 議定單價屬採購敏感資料；呼叫端是採購單 _Form，故採購單可讀亦放行。
            if (!await Permissions.CanReadAny(MenuKey, "PurchaseOrder")) return Forbid();

            var date = orderDate ?? _dt.GetLocalDate();

            var price = await Db.VendorPriceLists
                .AsNoTracking()
                .Where(v => v.SupplierCode  == supplierCode
                         && v.ProductCode   == productCode
                         && v.CurrencyCode  == currencyCode
                         && v.IsActive
                         && v.ValidFrom     <= date
                         && (v.ValidTo == null || v.ValidTo >= date))
                .OrderByDescending(v => v.ValidFrom)
                .Select(v => new { v.UnitPrice, v.MinQty, v.ValidTo })
                .FirstOrDefaultAsync();

            if (price == null) return Json(new { found = false });
            return Json(new { found = true, price.UnitPrice, price.MinQty, price.ValidTo });
        }

        // ── Private ─────────────────────────────────────────────────────────

        private async Task PrepareDropdowns()
        {
            ViewBag.SupplierOptions = await Db.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            ViewBag.CurrencyOptions = await Db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName })
                .ToListAsync();
        }

        private async Task PrepareFormDropdowns(VendorPriceList vm)
        {
            await PrepareDropdowns();

            ViewBag.ProductOptions = await Db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var base_ = await _currency.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(base_)) return 2;
            var map = await _currency.GetAllDecimalPlacesAsync();
            return map.TryGetValue(base_, out var dp) ? dp : 2;
        }
    }
}
