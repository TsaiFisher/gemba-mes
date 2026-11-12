using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using System.Globalization;

namespace SmartFactory.MES.Modules.QCR.Controllers
{
    /// <summary>
    /// 檢驗規範主檔（對應舊系統 QNO01 + QIT01）。
    /// 含規格項目 InspectionSpecItem 的 inline 管理。
    /// 全域（ProductCode/SupplierCode 為選填過濾鍵）+ 廠別（PlantId）。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class InspectionSpecController : ModuleControllerBase
    {
        private const string MenuKey = "InspectionSpec";
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public InspectionSpecController(IStringLocalizer<ProjectZSharedResource> localizer)
            => _localizer = localizer;

        public async Task<IActionResult> Index(
            int? pageNumber = 1, string sortOrder = "created_desc",
            string searchText = "", bool searched = false, int pageSize = 20)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.CurrentSort    = sortOrder;
            ViewBag.SearchText     = searchText;
            ViewBag.Searched       = searched;
            ViewBag.CurrentPageSize = pageSize;

            await PrepareIndexPermissionsAsync(MenuKey);
            if (!searched) return View((PaginatedList<InspectionSpec>?)null);

            var plantId = UserContext.PlantId;
            var q = Db.InspectionSpecs.AsNoTracking()
                .Where(s => s.PlantId == plantId)
                .Include(s => s.Items)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchText))
                q = q.Where(s => s.SpecCode.Contains(searchText)
                              || s.SpecName.Contains(searchText)
                              || (s.ProductCode != null && s.ProductCode.Contains(searchText)));

            q = sortOrder switch
            {
                "code_asc"  => q.OrderBy(s => s.SpecCode),
                "name_asc"  => q.OrderBy(s => s.SpecName),
                "created_asc" => q.OrderBy(s => s.CreatedAt),
                _           => q.OrderByDescending(s => s.CreatedAt)
            };

            return View(await PaginatedList<InspectionSpec>.CreateAsync(q, pageNumber ?? 1, pageSize));
        }

        private async Task PrepareFormDropdownsAsync()
        {
            ViewBag.ProductOptions = await Db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductCode)
                .Select(p => new { p.ProductCode, p.ProductName })
                .ToListAsync();

            ViewBag.SupplierOptions = await Db.Companies
                .AsNoTracking()
                .Where(c => c.IsSupplier && c.IsActive)
                .OrderBy(c => c.CompanyCode)
                .Select(c => new { c.CompanyCode, c.CompanyName })
                .ToListAsync();

            ViewBag.InstrumentOptions = await Db.Instruments
                .AsNoTracking()
                .Where(i => i.IsActive)
                .OrderBy(i => i.InstrumentCode)
                .Select(i => new { i.InstrumentCode })
                .ToListAsync();
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareIndexPermissionsAsync(MenuKey);
            await PrepareFormDropdownsAsync();
            return IsAjax
                ? PartialView("_Form", new InspectionSpec { PlantId = UserContext.PlantId })
                : View("_Form", new InspectionSpec { PlantId = UserContext.PlantId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromForm] InspectionSpec model,
            [FromForm] List<InspectionSpecItem>? Items)
        {
            if (!await Permissions.CanCreate(MenuKey))
                return JsonFail(_localizer["NoCreatePermission"].Value);

            if (!ModelState.IsValid)
                return JsonFail(string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));

            var plantId = UserContext.PlantId;
            if (await Db.InspectionSpecs.AnyAsync(s => s.PlantId == plantId && s.SpecCode == model.SpecCode))
                return JsonFail(_localizer["DuplicateCode"].Value);

            model.SpecId    = Guid.NewGuid().ToString("N");
            model.PlantId   = plantId;
            model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;

            if (Items != null)
            {
                var seq = 1;
                foreach (var item in Items.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)))
                {
                    item.SpecItemId = Guid.NewGuid().ToString("N");
                    item.SpecId     = model.SpecId;
                    item.SeqNo      = seq++;
                    model.Items.Add(item);
                }
            }

            Db.InspectionSpecs.Add(model);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var plantId = UserContext.PlantId;
            var spec = await Db.InspectionSpecs
                .Include(s => s.Items.OrderBy(i => i.SeqNo))
                .FirstOrDefaultAsync(s => s.SpecId == id && s.PlantId == plantId);
            if (spec == null) return IsAjax ? JsonFail(_localizer["RecordNotFound"].Value) : NotFound();

            await PrepareIndexPermissionsAsync(MenuKey);
            await PrepareFormDropdownsAsync();
            return IsAjax ? PartialView("_Form", spec) : View("_Form", spec);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [FromForm] InspectionSpec model,
            [FromForm] List<InspectionSpecItem>? Items)
        {
            if (!await Permissions.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoUpdatePermission"].Value);

            var plantId = UserContext.PlantId;
            var spec = await Db.InspectionSpecs
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.SpecId == id && s.PlantId == plantId);
            if (spec == null) return JsonFail(_localizer["RecordNotFound"].Value);

            spec.SpecName              = model.SpecName;
            spec.ProductCode           = model.ProductCode;
            spec.SupplierCode          = model.SupplierCode;
            spec.VerificationLevel     = model.VerificationLevel;
            spec.SamplingType          = model.SamplingType;
            spec.TriggerMode           = model.TriggerMode;
            spec.PeriodHours           = model.PeriodHours;
            spec.RequiresFQC           = model.RequiresFQC;
            spec.RequiresRQC           = model.RequiresRQC;
            spec.NormalVl              = model.NormalVl;
            spec.TightenedVl           = model.TightenedVl;
            spec.ReducedVl             = model.ReducedVl;
            spec.PassesToReduce        = model.PassesToReduce;
            spec.PassesToNormal        = model.PassesToNormal;
            spec.SkipLotEnabled        = model.SkipLotEnabled;
            spec.SkipLotFrequency      = model.SkipLotFrequency;
            spec.PassesToSkipLot       = model.PassesToSkipLot;
            spec.IsActive              = model.IsActive;
            spec.Remarks               = model.Remarks;
            spec.UpdatedAt             = DateTime.UtcNow;

            // 重建 Items（全刪全加）
            Db.InspectionSpecItems.RemoveRange(spec.Items);
            spec.Items.Clear();

            if (Items != null)
            {
                var seq = 1;
                foreach (var item in Items.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)))
                {
                    spec.Items.Add(new InspectionSpecItem
                    {
                        SpecItemId      = Guid.NewGuid().ToString("N"),
                        SpecId          = spec.SpecId,
                        SeqNo           = seq++,
                        ItemName        = item.ItemName,
                        UomCode         = item.UomCode,
                        LowerSpec       = item.LowerSpec,
                        TargetValue     = item.TargetValue,
                        UpperSpec       = item.UpperSpec,
                        InstrumentCode1 = item.InstrumentCode1,
                        InstrumentCode2 = item.InstrumentCode2,
                        InstrumentCode3 = item.InstrumentCode3,
                        IsActive        = item.IsActive
                    });
                }
            }

            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // InspectionSpec 掛 shadow xmin（SamplingPlanService 累加器共用本實體）；
                // 並發「規格編輯 vs 產線記錄檢驗結果」會撞權杖，回友善訊息而非裸 500。
                return JsonFail(_localizer["ConcurrencyConflict"].Value);
            }
            return JsonOk(message: _localizer["SaveSuccess"].Value);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            if (!await Permissions.CanDelete(MenuKey))
                return JsonFail(_localizer["NoDeletePermission"].Value);

            var plantId = UserContext.PlantId;
            var spec = await Db.InspectionSpecs
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.SpecId == id && s.PlantId == plantId);
            if (spec == null) return JsonFail(_localizer["RecordNotFound"].Value);

            Db.InspectionSpecs.Remove(spec);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["DeleteSuccess"].Value);
        }
    }
}
