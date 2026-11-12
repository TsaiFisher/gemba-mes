using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 委外報價主檔（mfg-domain.md §22）— 全域主檔，建立委外採購單時自動帶入單價。
    /// 支援 4-level 特殊度查詢：廠商+品號+工藝+工序 > 廠商+品號+工藝 > 廠商+品號 > 廠商。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class OutsourceQuotationController : ModuleControllerBase
    {
        private const string MenuKey = "OutsourceQuotation";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IDocNumberService _docNumber;
        private readonly IUomService _uomService;
        private readonly ICurrencyService _currencyService;
        private readonly IOutsourceQuotationResolver _quotationResolver;

        public OutsourceQuotationController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            IDocNumberService docNumber,
            IUomService uomService,
            ICurrencyService currencyService,
            IOutsourceQuotationResolver quotationResolver)
        {
            _localizer = localizer;
            _docNumber = docNumber;
            _uomService = uomService;
            _currencyService = currencyService;
            _quotationResolver = quotationResolver;
        }

        public async Task<IActionResult> Index(
            string? supplierCode, string? productCode, string? routingId,
            bool activeOnly = true, bool searched = false,
            string sortOrder = "supplier_asc", int pageNumber = 1, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.SupplierCode  = supplierCode;
            ViewBag.ProductCode   = productCode;
            ViewBag.RoutingId     = routingId;
            ViewBag.ActiveOnly    = activeOnly;
            ViewBag.CurrentSort   = sortOrder;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            // 查詢列下拉需在「尚未查詢」時就備妥，否則首次進畫面選項為空。
            await PrepareDropdowns();
            if (!searched) return View((PaginatedList<OutsourceQuotation>?)null);

            var query = Db.OutsourceQuotations.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(supplierCode))
                query = query.Where(q => q.SupplierCode == supplierCode);
            if (!string.IsNullOrEmpty(productCode))
                query = query.Where(q => q.ProductCode == productCode);
            if (!string.IsNullOrEmpty(routingId))
                query = query.Where(q => q.RoutingId == routingId);
            if (activeOnly)
                query = query.Where(q => q.IsActive);

            query = sortOrder switch
            {
                "supplier_desc"    => query.OrderByDescending(q => q.SupplierCode).ThenBy(q => q.ProductCode),
                "product_asc"      => query.OrderBy(q => q.ProductCode).ThenBy(q => q.SupplierCode),
                "product_desc"     => query.OrderByDescending(q => q.ProductCode).ThenBy(q => q.SupplierCode),
                "effective_from"   => query.OrderBy(q => q.EffectiveFrom),
                "effective_from_desc" => query.OrderByDescending(q => q.EffectiveFrom),
                _                  => query.OrderBy(q => q.SupplierCode).ThenBy(q => q.ProductCode).ThenBy(q => q.EffectiveFrom)
            };

            var list = await PaginatedList<OutsourceQuotation>.CreateAsync(query, pageNumber, pageSize);
            return View(list);
        }

        public async Task<IActionResult> Form(string? id)
        {
            if (!await Permissions.CanCreate(MenuKey) && id == null)
                return Forbid();
            if (!await Permissions.CanUpdate(MenuKey) && id != null)
                return Forbid();

            OutsourceQuotation? model = null;
            if (id != null)
            {
                model = await Db.OutsourceQuotations.AsNoTracking()
                    .FirstOrDefaultAsync(q => q.QuotationId == id);
                if (model == null)
                    return IsAjax ? Json(new { success = false, message = _localizer["RecordNotFound"].Value }) : NotFound();
            }

            await PrepareDropdowns();
            return PartialView("_Form", model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(OutsourceQuotation model)
        {
            // 單號由伺服端採番：新增時表單 hidden 帶空字串，非可空字串屬性 QuotationId 會被隱含
            // [Required] 擋下（ModelState 恆 invalid、面板靜默重渲染建不出單）。isNew 仍以
            // model.QuotationId 是否為空判斷，不受此移除影響。
            ModelState.Remove(nameof(OutsourceQuotation.QuotationId));

            if (!ModelState.IsValid)
            {
                await PrepareDropdowns();
                return PartialView("_Form", model);
            }

            bool isNew = string.IsNullOrEmpty(model.QuotationId);

            if (isNew)
            {
                if (!await Permissions.CanCreate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoCreatePermission"].Value });

                model.QuotationId = await _docNumber.NextAsync("OutsourceQuotation", null);
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;
                Db.OutsourceQuotations.Add(model);
            }
            else
            {
                if (!await Permissions.CanUpdate(MenuKey))
                    return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });

                var existing = await Db.OutsourceQuotations.FirstOrDefaultAsync(q => q.QuotationId == model.QuotationId);
                if (existing == null)
                    return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

                existing.SupplierCode   = model.SupplierCode;
                existing.ProductCode    = model.ProductCode;
                existing.RoutingId      = model.RoutingId;
                existing.OperationSeq   = model.OperationSeq;
                existing.UnitPrice      = model.UnitPrice;
                // 逐欄位手抄的路徑漏一欄不會有任何錯誤——合約允收損耗率漏抄的話，
                // 編輯過的報價會靜靜變回「未約定」，之後轉出去的單全部不扣款。
                existing.AllowedScrapRate = model.AllowedScrapRate;
                existing.CurrencyCode   = model.CurrencyCode;
                existing.UomCode        = model.UomCode;
                existing.MinQty         = model.MinQty;
                existing.MaxQty         = model.MaxQty;
                existing.LeadDays       = model.LeadDays;
                existing.EffectiveFrom  = model.EffectiveFrom;
                existing.EffectiveTo    = model.EffectiveTo;
                existing.Remarks        = model.Remarks;
                existing.IsActive       = model.IsActive;
                existing.UpdatedAt      = DateTime.UtcNow;
            }

            await Db.SaveChangesAsync();
            return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return Json(new { success = false, message = _localizer["NoDeletePermission"].Value });

            var item = await Db.OutsourceQuotations.FirstOrDefaultAsync(q => q.QuotationId == id);
            if (item == null)
                return Json(new { success = false, message = _localizer["RecordNotFound"].Value });

            Db.OutsourceQuotations.Remove(item);
            await Db.SaveChangesAsync();
            return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
        }

        /// <summary>委外採購單建立時的 API 查詢：依 4-level 優先度自動帶入單價</summary>
        [HttpGet]
        public async Task<IActionResult> LookupPrice(string supplierCode, string? productCode,
            string? routingId, int? operationSeq)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();
            if (string.IsNullOrEmpty(supplierCode))
                return Json(new { found = false });

            var today = DateOnly.FromDateTime(DateTime.Today);

            // 4-level 優先查詢。2026-09-23 改走 IOutsourceQuotationResolver：
            // 這裡原本只排序不過濾，於是候選裡沒有任何一筆適用時仍會回傳第一筆
            // ——畫面帶進一個別的品號／途程的加工費，而批次產生委外單那一支
            // 早就修成「先過濾再排序」。同一組條件兩個答案，兩個都不報錯。
            var candidates = await _quotationResolver.LoadActiveAsync(supplierCode, today);
            var match      = _quotationResolver.Resolve(candidates, productCode, routingId, operationSeq);

            if (match == null)
                return Json(new { found = false });

            return Json(new
            {
                found            = true,
                unitPrice        = match.UnitPrice,
                allowedScrapRate = match.AllowedScrapRate,
                currencyCode     = match.CurrencyCode,
                uomCode          = match.UomCode,
                leadDays         = match.LeadDays
            });
        }

        private async Task PrepareDropdowns()
        {
            ViewBag.SupplierOptions = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Company>()
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            ViewBag.ProductOptions = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Product>()
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.CurrencyOptions = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Currency>()
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.CurrencyCode)
                .Select(c => new { c.CurrencyCode, c.CurrencyName })
                .ToListAsync();

            ViewBag.UomOptions = await Db.Set<SmartFactory.MES.Modules.BAS.Models.Uom>()
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderBy(u => u.UomCode)
                .Select(u => new { u.UomCode, u.UomName })
                .ToListAsync();

            ViewBag.RoutingOptions = await Db.Routings.AsNoTracking()
                .OrderBy(r => r.RoutingId)
                .Select(r => new { r.RoutingId, r.RoutingName })
                .ToListAsync();

            ViewBag.CurrencyDecimals = await _currencyService.GetAllDecimalPlacesAsync();
            ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();
        }
    }
}
